﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using DanilovSoft.WebSocket;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace vRPC
{
    /// <summary>
    /// Контекст соединения Web-Сокета. Владеет соединением.
    /// </summary>
    [DebuggerDisplay(@"\{IsConnected = {IsConnected}\}")]
    public abstract class ManagedConnection : IDisposable
    {
        /// <summary>
        /// Максимальный размер фрейма который может передавать протокол. Сообщение может быть фрагментированно фреймами размером не больше этого значения.
        /// </summary>
        private const int WebSocketMaxFrameSize = 8192;
        private const string ProtocolHeaderErrorMessage = "Произошла ошибка десериализации заголовка от удалённой стороны.";
        /// <summary>
        /// Содержит имена методов прокси интерфейса без постфикса Async.
        /// </summary>
        private protected abstract IConcurrentDictionary<MethodInfo, string> _proxyMethodName { get; }
        /// <summary>
        /// Содержит все доступные для вызова экшены контроллеров.
        /// </summary>
        private readonly ControllerActionsDictionary _controllers;
        /// <summary>
        /// Для <see cref="Task"/> <see cref="Completion"/>.
        /// </summary>
        private readonly TaskCompletionSource<CloseReason> _completionTcs = new TaskCompletionSource<CloseReason>();
        /// <summary>
        /// Причина закрытия соединения. Это свойство возвращает <see cref="Completion"/>.
        /// </summary>
        public CloseReason DisconnectReason { get; private set; }
        /// <summary>
        /// Возвращает <see cref="Task"/> который завершается когда 
        /// соединение переходит в закрытое состояние.
        /// Не мутабельное свойство.
        /// Возвращает <see cref="DisconnectReason"/>.
        /// Не бросает исключения.
        /// </summary>
        public Task<CloseReason> Completion => _completionTcs.Task;
        //private readonly bool _isServer;
        public ServiceProvider ServiceProvider { get; private set; }
        /// <summary>
        /// Подключенный TCP сокет.
        /// </summary>
        private readonly ManagedWebSocket _socket;
        /// <summary>
        /// Коллекция запросов ожидающие ответ от удалённой стороны.
        /// </summary>
        private readonly RequestQueue _pendingRequests;
        public EndPoint LocalEndPoint => _socket.LocalEndPoint;
        public EndPoint RemoteEndPoint => _socket.RemoteEndPoint;
        /// <summary>
        /// Отправка сообщения <see cref="Message"/> должна выполняться только через этот канал.
        /// </summary>
        private readonly Channel<SerializedMessageToSend> _sendChannel;
        private int _disposed;
        private bool IsDisposed => Volatile.Read(ref _disposed) == 1;
        /// <summary>
        /// <see langword="true"/> если происходит остановка сервиса.
        /// Используется для проверки возможности начать новый запрос.
        /// </summary>
        private volatile bool _stopRequired;
        /// <summary>
        /// Предотвращает повторный вызов <see cref="RequireStop(string)"/>.
        /// </summary>
        private object StopRequiredLock => _completionTcs;
        /// <summary>
        /// Количество запросов для обработки и количество ответов для отправки.
        /// Для отслеживания грациозной остановки сервиса.
        /// </summary>
        private int _reqAndRespCount;
        /// <summary>
        /// Подписку на событие Disconnected нужно синхронизировать что-бы подписчики не пропустили момент обрыва.
        /// </summary>
        private object DisconnectEventObj => _sendChannel;
        private EventHandler<SocketDisconnectedEventArgs> _disconnected;
        /// <summary>
        /// Событие обрыва соединения. Может сработать только один раз.
        /// Если подписка на событие происходит к уже отключенному сокету то событие сработает сразу же.
        /// Гарантирует что событие не будет пропущено в какой бы момент не происходила подписка.
        /// </summary>
        public event EventHandler<SocketDisconnectedEventArgs> Disconnected
        {
            add
            {
                CloseReason closeReason = null;
                lock (DisconnectEventObj)
                {
                    if (DisconnectReason == null)
                    {
                        _disconnected += value;
                    }
                    else
                    // Подписка к уже отключенному сокету.
                    {
                        closeReason = DisconnectReason;
                    }
                }

                if(closeReason != null)
                {
                    value(this, new SocketDisconnectedEventArgs(closeReason));
                }
            }
            remove
            {
                // Отписываться можно без блокировки — делегаты потокобезопасны.
                _disconnected -= value;
            }
        }
        private protected abstract void BeforeInvokeController(Controller controller);
        private volatile bool _isConnected = true;
        /// <summary>
        /// Является <see langword="volatile"/>. Если значение – <see langword="false"/>, то можно узнать причину через свойство <see cref="DisconnectReason"/>.
        /// После <see langword="false"/> текущий экземпляр не может стать <see langword="true"/>.
        /// </summary>
        public bool IsConnected => _isConnected;
        /// <summary>
        /// Причина грациозного закрытия соединения которую устанавливает пользователь перед разъединением.
        /// </summary>
        private volatile string _closeDescription;

        // static ctor.
        static ManagedConnection()
        {
            //ManagedWebSocket.DefaultNoDelay = true;
            Debug.Assert(Marshal.SizeOf<HeaderDto>() <= 16, $"Структуру {nameof(HeaderDto)} лучше заменить на класс");
            //Debug.Assert(Marshal.SizeOf<RequestMessageDto>() <= 16, $"Структуру {nameof(RequestMessageDto)} лучше заменить на класс");
            //Debug.Assert(Marshal.SizeOf<RequestMessage>() <= 16, $"Структуру {nameof(RequestMessage)} лучше заменить на класс");
            //Debug.Assert(Marshal.SizeOf<ValueWebSocketReceiveExResult>() <= 16, $"Структуру {nameof(ValueWebSocketReceiveExResult)} лучше заменить на класс");
            //Debug.Assert(Marshal.SizeOf<CloseReason>() <= 16, $"Структуру {nameof(CloseReason)} лучше заменить на класс");

            // Прогрев сериализатора.
            ProtoBuf.Serializer.PrepareSerializer<HeaderDto>();
            ExtensionMethods.WarmupRequestMessageSerialization();
        }

        // ctor.
        internal ManagedConnection(ManagedWebSocket clientConnection, bool isServer, ServiceProvider serviceProvider, ControllerActionsDictionary controllers)
        {
            //_isServer = isServer;

            _socket = clientConnection;
            _pendingRequests = new RequestQueue();

            // IoC готов к работе.
            ServiceProvider = serviceProvider;

            // Копируем список контроллеров сервера.
            _controllers = controllers;

            _sendChannel = Channel.CreateUnbounded<SerializedMessageToSend>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = true, // Внимательнее с этим параметром!
                SingleReader = true,
                SingleWriter = false,
            });

            // Запустить диспетчер отправки сообщений.
            // Может спровоцировать Disconnect раньше.
            // Эта ситуация должна быть синхронизирована.
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                // Не бросает исключения.
                ((ManagedConnection)state).SenderLoop();
            }, this); // Без замыкания.

            // Лучше подписаться в конце.
            // Может сработать сразу.
            _socket.Disconnected += WebSocket_Disconnected;
        }

        private void WebSocket_Disconnected(object sender, DanilovSoft.WebSocket.SocketDisconnectedEventArgs e)
        {
            CloseReason closeReason;
            if (e.DisconnectReason.Gracifully)
            {
                closeReason = CloseReason.FromCloseFrame(e.DisconnectReason.CloseStatus, e.DisconnectReason.CloseDescription, e.DisconnectReason.AdditionalDescription);
            }
            else
            {
                closeReason = CloseReason.FromException(e.DisconnectReason.Error, e.DisconnectReason.AdditionalDescription);
            }
            AtomicDispose(closeReason);
        }

        /// <summary>
        /// Запрещает отправку новых запросов; Ожидает когда завершатся текущие запросы 
        /// и отправляет удалённой стороне сообщение о закрытии соединения с ожиданием подтверджения.
        /// Взводит <see cref="Completion"/>.
        /// Не бросает исключения.
        /// Потокобезопасно.
        /// </summary>
        /// <param name="closeDescription">Может быть <see langword="null"/>.</param>
        internal void RequireStop(string closeDescription)
        {
            lock (StopRequiredLock)
            {
                if (!_stopRequired)
                {
                    // Запретить выполнять новые запросы.
                    _stopRequired = true; // volatile.

                    // Запомнить причину отключения что-бы позднее передать её удалённой стороне.
                    _closeDescription = closeDescription; // volatile.

                    if (Interlocked.Decrement(ref _reqAndRespCount) == -1)
                    // Нет ни одного ожадающего запроса.
                    {
                        // Можно безопасно остановить сокет.
                        // Не бросает исключения.
                        SendCloseAsync(closeDescription).GetAwaiter();
                    }
                    // Иначе другие потоки уменьшив переменную увидят что флаг стал -1
                    // Это будет соглашением о необходимости остановки.
                }
            }
        }

        /// <summary>
        /// Не бросает исключения.
        /// </summary>
        private void CloseReceived()
        {
            // Был получен Close. Это значит что веб сокет уже закрыт и нам остаётся только закрыть сервис.
            AtomicDispose(CloseReason.FromCloseFrame(_socket.CloseStatus, _socket.CloseStatusDescription, null));
        }

        /// <summary>
        /// Отправляет сообщение Close и ожидает ответный Close. Затем закрывает соединение.
        /// Не бросает исключения.
        /// </summary>
        private async Task SendCloseAsync(string closeDescription)
        {
            // Эту функцию вызывает тот поток который поймал флаг о необходимости завершения сервиса.

            try
            {
                // Отправить Close с ожиданием ответного Close.
                // Может бросить исключение если сокет уже в статусе Close.
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, closeDescription, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Оповестить об обрыве.
                AtomicDispose(CloseReason.FromException(ex));

                // Завершить поток.
                return;
            }

            // Благодаря событию WebSocket.Disconnect у нас гарантированно вызовется AtomicDispose.
        }

        /// <summary>
        /// Отправляет сообщение Close и ожидает ответный Close. Затем закрывает соединение.
        /// Не бросает исключения.
        /// </summary>
        private Task SendCloseAsync()
        {
            return SendCloseAsync(_closeDescription);
        }

        /// <summary>
        /// Происходит при обращении к проксирующему интерфейсу.
        /// </summary>
        internal object OnServerProxyCall(MethodInfo targetMethod, object[] args, string controllerName)
        {
            // Тип результата инкапсулированный в Task<T>.
            Type resultType = targetMethod.GetMethodReturnType();

            // Имя метода без постфикса Async.
            string remoteMethodName = GetProxyMethodName(targetMethod);

            // Создаём запрос для отправки.
            var requestToSend = new RequestMessage(resultType, $"{controllerName}/{remoteMethodName}", args);

            // Сериализуем запрос в память.
            SerializedMessageToSend serMsg = SerializeRequest(requestToSend);

            // Отправляем запрос.
            Task<object> taskObject = OnProxyCall(serMsg, requestToSend);

            return OnProxyCallConvert(targetMethod, resultType, taskObject);
        }

        /// <summary>
        /// Происходит при обращении к проксирующему интерфейсу.
        /// </summary>
        internal static object OnClientProxyCallStatic(ValueTask<ManagedConnection> contextTask, MethodInfo targetMethod, object[] args, string controllerName)
        {
            // Тип результата инкапсулированный в Task<T>.
            Type resultType = targetMethod.GetMethodReturnType();

            // Имя метода без постфикса Async.
            string remoteMethodName = ClientSideConnection.ProxyMethodName.GetOrAdd(targetMethod, m => m.GetNameTrimAsync());

            // Создаём запрос для отправки.
            var requestToSend = new RequestMessage(resultType, $"{controllerName}/{remoteMethodName}", args);

            // Сериализуем запрос в память. Лучше выполнить до подключения.
            SerializedMessageToSend serMsg = SerializeRequest(requestToSend);

            // Отправляем запрос.
            Task<object> taskObject = OnProxyCallAsync(contextTask, serMsg, requestToSend);

            return OnProxyCallConvert(targetMethod, resultType, taskObject);
        }

        private static Task<object> OnProxyCallAsync(ValueTask<ManagedConnection> contextTask, SerializedMessageToSend serializedMessage, RequestMessage requestMessage)
        {
            if(contextTask.IsCompleted)
            {
                // Отправляет запрос и получает результат от удалённой стороны.
                return contextTask.Result.OnProxyCall(serializedMessage, requestMessage);
            }
            else
            {
                return WaitForConnectAndCallProxy(contextTask, serializedMessage, requestMessage);
            }
        }

        private static async Task<object> WaitForConnectAndCallProxy(ValueTask<ManagedConnection> t, SerializedMessageToSend serializedMessage, RequestMessage requestMessage)
        {
            // Ждём завершение подключения к серверу.
            ManagedConnection context = await t.ConfigureAwait(false);

            // Отправляет запрос и получает результат от удалённой стороны.
            return await context.OnProxyCall(serializedMessage, requestMessage).ConfigureAwait(false);
        }

        private static object OnProxyCallConvert(MethodInfo targetMethod, Type resultType, Task<object> taskObject)
        {
            if (targetMethod.IsAsyncMethod())
            // Возвращаемый тип функции интерфейса — Task.
            {
                if (targetMethod.ReturnType.IsGenericType)
                // У задачи есть результат.
                {
                    // Task<object> должен быть преобразован в Task<T>.
                    return TaskConverter.ConvertTask(taskObject, resultType, targetMethod.ReturnType);
                }
                else
                {
                    if (targetMethod.ReturnType != typeof(ValueTask))
                    {
                        // Если возвращаемый тип интерфейса – Task то можно вернуть Task<object>.
                        return taskObject;
                    }
                    else
                    {
                        return new ValueTask(taskObject);
                    }
                }
            }
            else
            // Была вызвана синхронная функция.
            {
                // Результатом может быть исключение.
                object finalResult = taskObject.GetAwaiter().GetResult();
                return finalResult;
            }
        }

        /// <summary>
        /// Происходит при обращении к проксирующему интерфейсу. Отправляет запрос и ожидает его ответ.
        /// </summary>
        /// /// <param name="resultType">Тип в который будет десериализован результат запроса.</param>
        internal Task<object> OnProxyCall(SerializedMessageToSend serializedMessage, RequestMessage requestMessage)
        {
            ThrowIfDisposed();
            ThrowIfStopRequired();

            // Добавить запрос в словарь для дальнейшей связки с ответом.
            RequestAwaiter tcs = _pendingRequests.AddRequest(requestMessage, out ushort uid);

            // Назначить запросу уникальный идентификатор.
            serializedMessage.Uid = uid;

            // Планируем отправку запроса.
            QueueSendMessage(serializedMessage);

            // Ожидаем результат от потока поторый читает из сокета.
            return WaitForAwaiterAsync(tcs);
        }

        private async Task<object> WaitForAwaiterAsync(RequestAwaiter tcs)
        {
            // Ожидаем результат от потока поторый читает из сокета.
            // Валидным результатом может быть исключение.
            object rawResult = await tcs;

            // Успешно получили результат без исключений.
            return rawResult;
        }

        /// <summary>
        /// Возвращает имя метода без постфикса Async.
        /// </summary>
        /// <param name="method"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string GetProxyMethodName(MethodInfo method)
        {
            return _proxyMethodName.GetOrAdd(method, valueFactory: m => m.GetNameTrimAsync());
        }

        /// <summary>
        /// Запускает бесконечный цикл, в фоновом потоке, считывающий из сокета запросы и ответы.
        /// </summary>
        internal void StartReceivingLoop()
        {
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                // Не бросает исключения.
                ((ManagedConnection)state).ReceiveLoop();
                
            }, this); // Без замыкания.
        }

        private async void ReceiveLoop()
        {
            byte[] headerBuffer = new byte[HeaderDto.HeaderMaxSize];

            // Бесконечно обрабатываем сообщения сокета.
            while (!IsDisposed)
            {
                #region Читаем хедер

                ValueWebSocketReceiveExResult webSocketMessage;

                try
                {
                    // Читаем фрейм веб-сокета.
                    webSocketMessage = await _socket.ReceiveExAsync(headerBuffer, CancellationToken.None);
                }
                catch (Exception ex)
                // Обрыв соединения.
                {
                    // Оповестить об обрыве.
                    AtomicDispose(CloseReason.FromException(ex));

                    // Завершить поток.
                    return;
                }

                HeaderDto header = default;
                if (webSocketMessage.ReceiveResult.IsReceivedSuccessfully)
                {
                    if (webSocketMessage.MessageType != WebSocketMessageType.Close)
                    {
                        try
                        {
                            header = HeaderDto.DeserializeProtobuf(headerBuffer, 0, webSocketMessage.Count);
                        }
                        catch (Exception headerException)
                        // Не удалось десериализовать заголовок.
                        {
                            #region Отправка Close и выход

                            var protocolErrorException = new ProtocolErrorException(ProtocolHeaderErrorMessage, headerException);

                            // Сообщить потокам что обрыв произошел по вине удалённой стороны.
                            _pendingRequests.PropagateExceptionAndLockup(protocolErrorException);

                            try
                            {
                                // Отключаемся от сокета с небольшим таймаутом.
                                using var cts = new CancellationTokenSource(2000);
                                await _socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Ошибка десериализации заголовка.", cts.Token);
                            }
                            catch (Exception ex)
                            // Злой обрыв соединения.
                            {
                                // Оповестить об обрыве.
                                AtomicDispose(CloseReason.FromException(ex));

                                // Завершить поток.
                                return;
                            }

                            // Оповестить об обрыве.
                            AtomicDispose(CloseReason.FromException(protocolErrorException));

                            // Завершить поток.
                            return;
                            #endregion
                        }
                    }
                    else
                    // Получен Close.
                    {
                        CloseReceived();

                        // Завершить поток.
                        return;
                    }
                }
                else
                // Ошибка сокета при получении хедера.
                {
                    // Оповестить об обрыве.
                    AtomicDispose(CloseReason.FromException(webSocketMessage.ReceiveResult.ToException()));

                    // Завершить поток.
                    return;
                }
                #endregion

                if (!header.Equals(default(HeaderDto)))
                {
                    // Стрим который будет содержать сообщение целиком.
                    using var messageStream = new MemoryPoolStream(header.ContentLength);

                    // Обязательно установить размер стрима. Можно не очищать – буффер будет перезаписан.
                    messageStream.SetLength(header.ContentLength, clear: false);

                    int offset = 0;
                    int receiveMessageBytesLeft = header.ContentLength;
                    byte[] messageStreamBuffer = messageStream.DangerousGetBuffer();

                    do // Читаем и склеиваем фреймы веб-сокета пока не EndOfMessage.
                    {
                        #region Пока не EndOfMessage записывать в буфер памяти

                        #region Читаем фрейм веб-сокета

                        try
                        {
                            // Читаем фрейм веб-сокета.
                            webSocketMessage = await _socket.ReceiveExAsync(
                                messageStreamBuffer.AsMemory().Slice(offset, receiveMessageBytesLeft), CancellationToken.None);
                        }
                        catch (Exception ex)
                        // Обрыв соединения.
                        {
                            // Оповестить об обрыве.
                            AtomicDispose(CloseReason.FromException(ex));

                            // Завершить поток.
                            return;
                        }
                        #endregion

                        if (webSocketMessage.ReceiveResult.IsReceivedSuccessfully)
                        {
                            if (webSocketMessage.MessageType != WebSocketMessageType.Close)
                            {
                                offset += webSocketMessage.Count;
                                receiveMessageBytesLeft -= webSocketMessage.Count;
                            }
                            else
                            // Другая сторона закрыла соединение.
                            {
                                CloseReceived();

                                // Завершить поток.
                                return;
                            }
                        }
                        else
                        // Обрыв соединения.
                        {
                            // Оповестить об обрыве.
                            AtomicDispose(CloseReason.FromException(webSocketMessage.ReceiveResult.ToException()));

                            // Завершить поток.
                            return;
                        }

                        #endregion

                    } while (!webSocketMessage.EndOfMessage);

                    #region Обработка Payload

                    if (header.StatusCode == StatusCode.Request)
                    // Получен запрос.
                    {
                        #region Выполнение запроса

                        #region Десериализация запроса

                        RequestMessageDto receivedRequest;
                        try
                        {
                            // Десериализуем запрос.
                            //receivedRequest = ExtensionMethods.DeserializeRequestJson(messageStream);
                            receivedRequest = ExtensionMethods.DeserializeRequestBson(messageStream);
                        }
                        catch (Exception ex)
                        // Ошибка десериализации запроса.
                        {
                            #region Игнорируем запрос

                            // Подготовить ответ с ошибкой.
                            var errorResponse = new ResponseMessage(header.Uid, new InvalidRequestResult($"Не удалось десериализовать запрос. Ошибка: \"{ex.Message}\"."));

                            // Передать на отправку результат с ошибкой через очередь.
                            QueueSendResponse(errorResponse);

                            // Вернуться к чтению заголовка.
                            continue;
                            #endregion
                        }
                        #endregion

                        #region Выполнение запроса

                        // Установить контекст запроса.
                        var request = new RequestContext(header, receivedRequest);

                        // Начать выполнение запроса в отдельном потоке.
                        StartProcessRequest(request);
                        #endregion

                        #endregion
                    }
                    else
                    // Получен ответ на запрос.
                    {
                        #region Передача другому потоку ответа на запрос

                        // Удалить запрос из словаря.
                        if (_pendingRequests.TryRemove(header.Uid, out RequestAwaiter reqAwaiter))
                        // Передать ответ ожидающему потоку.
                        {
                            #region Передать ответ ожидающему потоку

                            if (header.StatusCode == StatusCode.Ok)
                            // Запрос на удалённой стороне был выполнен успешно.
                            {
                                #region Передать успешный результат

                                if (reqAwaiter.Request.ReturnType != typeof(void))
                                {
                                    // Десериализатор в соответствии с ContentEncoding.
                                    var deserializer = header.GetDeserializer();

                                    bool deserialized;
                                    object rawResult = null;
                                    try
                                    {
                                        rawResult = deserializer(messageStream, reqAwaiter.Request.ReturnType);
                                        deserialized = true;
                                    }
                                    catch (Exception deserializationException)
                                    {
                                        var protocolErrorException = new ProtocolErrorException(
                                            $"Ошибка десериализации ответа на запрос \"{reqAwaiter.Request.ActionName}\".", deserializationException);

                                        // Сообщить ожидающему потоку что произошла ошибка при разборе ответа удаленной стороны.
                                        reqAwaiter.TrySetException(protocolErrorException);

                                        deserialized = false;
                                    }

                                    if (deserialized)
                                    {
                                        // Передать результат ожидающему потоку.
                                        reqAwaiter.TrySetResult(rawResult);
                                    }
                                }
                                else
                                // void.
                                {
                                    reqAwaiter.TrySetResult(null);
                                }
                                #endregion
                            }
                            else
                            // Сервер прислал код ошибки.
                            {
                                // Телом ответа в этом случае будет строка.
                                string errorMessage = messageStream.ReadAsString();

                                // Сообщить ожидающему потоку что удаленная сторона вернула ошибку в результате выполнения запроса.
                                reqAwaiter.TrySetException(new BadRequestException(errorMessage, header.StatusCode));
                            }
                            #endregion

                            // Получен ожидаемый ответ на запрос.
                            if (Interlocked.Decrement(ref _reqAndRespCount) != -1)
                            {
                                continue;
                            }
                            else
                            // Пользователь запросил остановку сервиса.
                            {
                                // Не бросает исключения.
                                await SendCloseAsync();

                                // Завершить поток.
                                return;
                            }
                        }

                        #endregion
                    }
                    #endregion
                }
                else
                // Ошибка в хедере.
                {
                    #region Отправка Close

                    var protocolErrorException = new ProtocolErrorException("Не удалось десериализовать полученный заголовок сообщения.");

                    // Сообщить потокам что обрыв произошел по вине удалённой стороны.
                    _pendingRequests.PropagateExceptionAndLockup(protocolErrorException);

                    try
                    {
                        // Отключаемся от сокета с небольшим таймаутом.
                        using var cts = new CancellationTokenSource(1000);
                        await _socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Не удалось десериализовать полученный заголовок сообщения.", cts.Token);
                    }
                    catch (Exception ex)
                    // Злой обрыв соединения.
                    {
                        // Оповестить об обрыве.
                        AtomicDispose(CloseReason.FromException(ex));

                        // Завершить поток.
                        return;
                    }

                    // Оповестить об обрыве.
                    AtomicDispose(CloseReason.FromException(protocolErrorException));

                    // Завершить поток.
                    return;

                    #endregion
                }
            }
        }

        /// <summary>
        /// Сериализует сообщение в новом потоке и добавляет в очередь на отправку.
        /// Не должно бросать исключения(!).
        /// </summary>
        /// <param name="responseToSend"></param>
        private void QueueSendResponse(ResponseMessage responseToSend)
        {
            ThreadPool.UnsafeQueueUserWorkItem(state => 
            {
                var tuple = ((ManagedConnection thisRef, ResponseMessage responseToSend))state;

                // Сериализуем.
                SerializedMessageToSend serializedMessage = SerializeResponse(tuple.responseToSend);

                // Ставим в очередь.
                tuple.thisRef.QueueSendMessage(serializedMessage);

            }, (this, responseToSend));
        }

        /// <summary>
        /// Добавляет хэдер и передает на отправку другому потоку.
        /// Не бросает исключения.
        /// </summary>
        private void QueueSendMessage(SerializedMessageToSend messageToSend)
        {
            Debug.Assert(messageToSend != null);

            // На текущем этапе сокет может быть уже уничтожен другим потоком.
            // В этом случае можем беспоследственно проигнорировать отправку; вызывающий получит исключение через RequestAwaiter.
            if (!IsDisposed)
            {
                // Сериализуем хедер. Не бросает исключения.
                AppendHeader(messageToSend);
                
                // Передать на отправку.
                // Из-за AllowSynchronousContinuations частично начнёт отправку текущим потоком(!).
                if (_sendChannel.Writer.TryWrite(messageToSend))
                    return;
                else
                    messageToSend.Dispose(); // Канал уже закрыт (был вызван Dispose).
            }
            else
            {
                messageToSend.Dispose();
            }
        }

        /// <summary>
        /// Сериализует сообщение в память. Может бросить исключение сериализации.
        /// </summary>
        private static SerializedMessageToSend SerializeRequest(RequestMessage requestToSend)
        {
            var serMsg = new SerializedMessageToSend(requestToSend);
            try
            {
                var request = new RequestMessageDto(requestToSend.ActionName, requestToSend.Args);
                //ExtensionMethods.SerializeObjectJson(serMsg.MemoryStream, request);
                ExtensionMethods.SerializeObjectBson(serMsg.MemPoolStream, request);
            }
            catch
            {
                serMsg.Dispose();
                throw;
            }
            return serMsg;
        }

        /// <summary>
        /// Сериализует сообщение в память. Может бросить исключение сериализации.
        /// </summary>
        private static SerializedMessageToSend SerializeResponse(ResponseMessage responseToSend)
        {
            var serMsg = new SerializedMessageToSend(responseToSend);
            try
            {
                if (responseToSend.Result is IActionResult actionResult)
                {
                    var actionContext = new ActionContext(responseToSend.ReceivedRequest, serMsg.MemPoolStream);
                    
                    // Сериализуем ответ.
                    actionResult.ExecuteResult(actionContext);
                    serMsg.StatusCode = actionContext.StatusCode;
                    serMsg.ContentEncoding = actionContext.ProducesEncoding;
                }
                else
                {
                    // Сериализуем ответ.
                    responseToSend.ReceivedRequest.ActionToInvoke.Serializer(serMsg.MemPoolStream, responseToSend.Result);
                    serMsg.StatusCode = StatusCode.Ok;
                    serMsg.ContentEncoding = responseToSend.ReceivedRequest.ActionToInvoke.ProducesEncoding;
                }
            }
            catch
            {
                serMsg.Dispose();
                throw;
            }
            return serMsg;
        }

        /// <summary>
        /// Сериализует хэдер в стрим сообщения. Не бросает исключения.
        /// </summary>
        private static void AppendHeader(SerializedMessageToSend messageToSend)
        {
            HeaderDto header = CreateHeader(messageToSend);

            // Записать заголовок в конец стрима. Не бросает исключения.
            header.SerializeProtoBuf(messageToSend.MemPoolStream, out int headerSize);

            // Запомним размер хэдера.
            messageToSend.HeaderSize = headerSize;
        }

        private static HeaderDto CreateHeader(SerializedMessageToSend messageToSend)
        {
            Debug.Assert(messageToSend != null);

            if (messageToSend.MessageToSend is ResponseMessage responseToSend)
            // Создать хедер ответа на запрос.
            {
                Debug.Assert(messageToSend.StatusCode != null, "StatusCode ответа не может быть Null");

                return HeaderDto.FromResponse(responseToSend.Uid, messageToSend.StatusCode.Value, (int)messageToSend.MemPoolStream.Length, messageToSend.ContentEncoding);
            }
            else
            // Создать хедер для нового запроса.
            {
                return HeaderDto.CreateRequest(messageToSend.Uid, (int)messageToSend.MemPoolStream.Length);
            }
        }

        /// <summary>
        /// Принимает заказы на отправку и отправляет в сокет. Запускается из конструктора. Не бросает исключения.
        /// </summary>
        /// <returns></returns>
        private async void SenderLoop() // Точка входа нового потока.
        {
            while (!IsDisposed)
            {
                // Ждём сообщение для отправки.
                if (await _sendChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    // Всегда true — у нас только один читатель.
                    _sendChannel.Reader.TryRead(out SerializedMessageToSend serializedMessage);

                    // Теперь мы владеем этим объектом.
                    using (serializedMessage)
                    {
                        if (serializedMessage.MessageToSend is RequestMessage)
                        {
                            // Должны получить ответ на этот запрос.
                            if (Interlocked.Increment(ref _reqAndRespCount) == 0)
                            // Значение было -1, значит происходит остановка и сокет уже уничтожен.
                            {
                                return;
                            }
                        }

                        byte[] streamBuffer = serializedMessage.MemPoolStream.DangerousGetBuffer();

                        // Размер сообщения без заголовка.
                        int messageSize = (int)serializedMessage.MemPoolStream.Length - serializedMessage.HeaderSize;

                        #region Отправка заголовка

                        SocketError socketError;
                        try
                        {
                            // Заголовок лежит в конце стрима.
                            socketError = await _socket.SendExAsync(streamBuffer.AsMemory(messageSize, serializedMessage.HeaderSize),
                                WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        // Обрыв соединения.
                        {
                            // Оповестить об обрыве.
                            AtomicDispose(CloseReason.FromException(ex));

                            // Завершить поток.
                            return;
                        }
                        #endregion

                        if (socketError == SocketError.Success)
                        {
                            #region Отправка тела сообщения (запрос или ответ на запрос)

                            // Отправляем сообщение по частям.
                            int offset = 0;
                            int bytesLeft = messageSize;
                            do
                            {
                                // TODO возможно нет смысла.
                                #region Фрагментируем отправку

                                bool endOfMessage;
                                int countToSend = WebSocketMaxFrameSize;
                                if (countToSend >= bytesLeft)
                                {
                                    countToSend = bytesLeft;
                                    endOfMessage = true;
                                }
                                else
                                    endOfMessage = false;

                                try
                                {
                                    socketError = await _socket.SendExAsync(streamBuffer.AsMemory(offset, countToSend),
                                        WebSocketMessageType.Binary, endOfMessage, CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                // Обрыв соединения.
                                {
                                    // Оповестить об обрыве.
                                    AtomicDispose(CloseReason.FromException(ex));

                                    // Завершить поток.
                                    return;
                                }

                                if (socketError == SocketError.Success)
                                {
                                    if (endOfMessage)
                                        break;

                                    bytesLeft -= countToSend;
                                    offset += countToSend;
                                }
                                else
                                {
                                    // Оповестить об обрыве.
                                    AtomicDispose(CloseReason.FromException(socketError.ToException()));

                                    // Завершить поток.
                                    return;
                                }
                                #endregion
                            } while (bytesLeft > 0);
                            #endregion
                        }
                        else
                        {
                            // Оповестить об обрыве.
                            AtomicDispose(CloseReason.FromException(socketError.ToException()));

                            // Завершить поток.
                            return;
                        }

                        if (serializedMessage.MessageToSend is ResponseMessage)
                        // Ответ успешно отправлен.
                        {
                            if (Interlocked.Decrement(ref _reqAndRespCount) != -1)
                            {
                                continue;
                            }
                            else
                            // Пользователь запросил остановку сервиса.
                            {
                                // Не бросает исключения.
                                await SendCloseAsync();

                                // Завершить поток.
                                return;
                            }
                        }
                    }
                }
                else
                // Dispose закрыл канал.
                {
                    // Завершить поток.
                    return;
                }
            }
        }

        /// <summary>
        /// Вызывает запрошенный метод контроллера и возвращает результат.
        /// Результатом может быть IActionResult или Raw объект или исключение.
        /// </summary>
        /// <exception cref="BadRequestException"/>
        private async ValueTask<object> InvokeControllerAsync(RequestContext receivedRequest)
        {
            // Находим контроллер по словарю без блокировки.
            if (TryGetRequestedController(receivedRequest, out string controllerName, out string actionName, out Type controllerType))
            {
                // Ищем делегат запрашиваемой функции по словарю без блокировки.
                if (_controllers.TryGetValue(controllerType, actionName, out ControllerAction action))
                {
                    // Контекст запроса запоминает запрашиваемый метод.
                    receivedRequest.ActionToInvoke = action;

                    // Проверить доступ к функции.
                    if (InvokeMethodPermissionCheck(action.TargetMethod, controllerType, out IActionResult permissionError))
                    {
                        // Блок IoC выполнит Dispose всем созданным экземплярам.
                        using IServiceScope scope = ServiceProvider.CreateScope();

                        // Активируем контроллер через IoC.
                        var controller = (Controller)scope.ServiceProvider.GetRequiredService(controllerType);
                        //{
                        // Подготавливаем контроллер.
                        BeforeInvokeController(controller);

                        // Мапим и десериализуем аргументы по их именам.
                        //object[] args = DeserializeParameters(action.TargetMethod.GetParameters(), receivedRequest);

                        ParameterInfo[] methodParameters = action.TargetMethod.GetParameters();
                        if (ValidateArguments(methodParameters, receivedRequest.RequestDto, out var paramsError))
                        {
                            // Мапим и десериализуем аргументы по их порядку.
                            object[] args = DeserializeArguments(methodParameters, receivedRequest.RequestDto);

                            // Вызов метода контроллера.
                            object controllerResult = action.TargetMethod.InvokeFast(controller, args);

                            if (controllerResult != null)
                            {
                                // Извлекает результат из Task'а.
                                controllerResult = await DynamicAwaiter.WaitAsync(controllerResult).ConfigureAwait(false);
                            }

                            // Результат успешно получен без исключения.
                            return controllerResult;
                        }
                        else
                            return paramsError;
                    }
                    else
                        return permissionError;
                }
                else
                    return new NotFoundResult($"Unable to find requested action \"{receivedRequest.RequestDto.ActionName}\".");
            }
            else
                return new NotFoundResult($"Unable to find requested controller \"{controllerName}\".");
        }

        /// <summary>
        /// Проверяет доступность запрашиваемого метода для удаленного пользователя.
        /// </summary>
        /// <exception cref="BadRequestException"/>
        protected abstract bool InvokeMethodPermissionCheck(MethodInfo method, Type controllerType, out IActionResult permissionError);

        /// <summary>
        /// Пытается найти запрашиваемый пользователем контроллер.
        /// </summary>
        private bool TryGetRequestedController(RequestContext request, out string controllerName, out string actionName, out Type controllerType)
        {
            int index = request.RequestDto.ActionName.IndexOf('/');
            if (index == -1)
            {
                controllerName = "Home";
                actionName = request.RequestDto.ActionName;
            }
            else
            {
                controllerName = request.RequestDto.ActionName.Substring(0, index);
                actionName = request.RequestDto.ActionName.Substring(index + 1);
            }

            // Ищем контроллер в кэше.
            if (_controllers.Controllers.TryGetValue(controllerName, out controllerType))
                return true;

            return false;
        }

        private bool ValidateArguments(ParameterInfo[] targetArguments, RequestMessageDto request, out IActionResult error)
        {
            if (request.Args.Length == targetArguments.Length)
            {
                error = null;
                return true;
            }
            error = new BadRequestResult("Argument count mismatch.");
            return false;
        }

        ///// <summary>
        ///// Производит маппинг аргументов запроса в соответствии с делегатом.
        ///// </summary>
        ///// <param name="method">Метод который будем вызывать.</param>
        //private object[] DeserializeParameters(ParameterInfo[] targetArguments, RequestMessage request)
        //{
        //    object[] args = new object[targetArguments.Length];

        //    for (int i = 0; i < targetArguments.Length; i++)
        //    {
        //        ParameterInfo p = targetArguments[i];
        //        var arg = request.Args.FirstOrDefault(x => x.ParameterName.Equals(p.Name, StringComparison.InvariantCultureIgnoreCase));
        //        if (arg == null)
        //            throw new BadRequestException($"Argument \"{p.Name}\" missing.");

        //        args[i] = arg.Value.ToObject(p.ParameterType);
        //    }
        //    return args;
        //}

        // TODO заменить исключение.
        /// <summary>
        /// Производит маппинг аргументов по их порядку.
        /// </summary>
        /// <param name="method">Метод который будем вызывать.</param>
        private object[] DeserializeArguments(ParameterInfo[] targetArguments, RequestMessageDto request)
        {
            object[] args = new object[targetArguments.Length];
            for (int i = 0; i < targetArguments.Length; i++)
            {
                ParameterInfo p = targetArguments[i];
                var arg = request.Args[i];
                args[i] = arg.ToObject(p.ParameterType);
            }
            return args;
        }

        /// <summary>
        /// В новом потоке выполняет запрос и отправляет ему результат или ошибку.
        /// </summary>
        private void StartProcessRequest(RequestContext request)
        {
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var tuple = (Tuple<ManagedConnection, RequestContext>)state;

                // Не бросает исключения.
                tuple.Item1.StartProcessRequestThread(tuple.Item2);

            }, state: Tuple.Create(this, request)); // Без замыкания.
        }

        /// <summary>
        /// Выполняет запрос и отправляет результат или ошибку.
        /// </summary>
        /// <param name="requestContext"></param>
        private void StartProcessRequestThread(RequestContext requestContext) // Точка входа потока из пула.
        {
            // В редких случаях сокет может быть уже закрыт, например по таймауту,
            // в этом случае ничего выполнять не нужно.
            if (!IsDisposed)
            {
                // Увеличить счетчик запросов.
                if (Interlocked.Increment(ref _reqAndRespCount) > 0)
                {
                    // Выполняет запрос и возвращает ответ.
                    var t = GetResponseAsync(requestContext);
                    if (t.IsCompleted)
                    {
                        ResponseMessage responseMessage = t.Result; // Не бросает исключения.

                        // Не бросает исключения.
                        SerializeAndSendResponse(responseMessage, requestContext);
                    }
                    else
                    {
                        WaitResponseAndSendAsync(t, requestContext);
                    }
                }
                else
                // Значение было -1, значит происходит остановка. Выполнять запрос не нужно.
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Не бросает исключения.
        /// </summary>
        private void SerializeAndSendResponse(ResponseMessage responseMessage, RequestContext requestContext)
        {
            // Не бросает исключения.
            SerializedMessageToSend responseToSend = SerializeResponse(responseMessage, requestContext);

            // Не бросает исключения.
            QueueSendMessage(responseToSend);
        }

        private async void WaitResponseAndSendAsync(ValueTask<ResponseMessage> t, RequestContext requestContext)
        {
            // Не бросает исключения.
            // Выполняет запрос и возвращает ответ.
            ResponseMessage responseMessage = await t.ConfigureAwait(false);

            // Не бросает исключения.
            SerializeAndSendResponse(responseMessage, requestContext);
        }

        /// <summary>
        /// Выполняет запрос клиента и инкапсулирует результат в <see cref="Response"/>.
        /// Не бросает исключения.
        /// </summary>
        private ValueTask<ResponseMessage> GetResponseAsync(RequestContext requestContext)
        {
            // Функция обязательно должна быть с ключевым словом async 
            // что-бы исключение могло агрегироваться в Task.
            ValueTask<object> t = InvokeControllerAsync(requestContext);
            if(t.IsCompletedSuccessfully)
            // Синхронно только в случае успеха.
            {
                // Результат контроллера. Может быть Task.
                var response = new ResponseMessage(requestContext, t.Result);
                return new ValueTask<ResponseMessage>(response);
            }
            else
            {
                return WaitForInvokeControllerAsync(t, requestContext);
            }
        }

        private static SerializedMessageToSend SerializeResponse(ResponseMessage response, RequestContext requestContext)
        {
            if (response == null)
            // Запрашиваемая функция выполнена успешно.
            {
                try
                {
                    return SerializeResponse(response);
                }
                catch (Exception ex)
                // Злая ошибка сериализации ответа. Аналогично ошибке 500.
                {
                    // Прервать отладку.
                    DebugOnly.Break();

                    // TODO залогировать.
                    Debug.WriteLine(ex);

                    // Вернуть результат с ошибкой.
                    response = new ResponseMessage(requestContext, new InternalErrorResult("Internal Server Error"));
                }

                // response содержит ошибку.
                return SerializeResponse(response);
            }
            else
            // response содержит ошибку.
            {
                // Сериализуется без исключения.
                return SerializeResponse(response);
            }
        }

        private async ValueTask<ResponseMessage> WaitForInvokeControllerAsync(ValueTask<object> t, RequestContext requestContext)
        {
            try
            {
                // Находит и выполняет запрашиваемую функцию.
                object rawResult = await t.ConfigureAwait(false);
                return new ResponseMessage(requestContext, rawResult);
            }
            catch (BadRequestException ex)
            {
                // Вернуть результат с ошибкой.
                return new ResponseMessage(requestContext, new BadRequestResult(ex.Message));
            }
            catch (Exception ex)
            // Злая ошибка обработки запроса. Аналогично ошибке 500.
            {
                // Прервать отладку.
                DebugOnly.Break();

                Debug.WriteLine(ex);

                // Вернуть результат с ошибкой.
                return new ResponseMessage(requestContext, new InternalErrorResult("Internal Server Error"));
            }
        }

        /// <summary>
        /// AggressiveInlining.
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (!IsDisposed)
                return;

            throw new ObjectDisposedException(GetType().FullName);
        }

        /// <summary>
        /// Не позволять начинать новый запрос если происходит остановка.
        /// AggressiveInlining.
        /// </summary>
        /// <exception cref="StopRequiredException"/>
        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfStopRequired()
        {
            if (!_stopRequired)
                return;

            throw new StopRequiredException();
        }

        /// <summary>
        /// Вызывает Dispose распространяя исключение <see cref="StopRequiredException"/> другим потокам.
        /// Потокобезопасно.
        /// Не бросает исключения.
        /// </summary>
        internal void CloseAndDispose(TimeSpan afterTimeout)
        {
            AtomicDispose(CloseReason.FromException(new StopRequiredException(afterTimeout)));
        }

        /// <summary>
        /// Потокобезопасно закрывает соединение и освобождает все ресурсы.
        /// </summary>
        public void Dispose()
        {
            DisposeManaged();
        }

        protected virtual void DisposeManaged()
        {
            AtomicDispose(CloseReason.FromException(new ObjectDisposedException(GetType().FullName), "Пользователь вызвал Dispose."));
        }

        /// <summary>
        /// Потокобезопасно освобождает ресурсы соединения. Вызывается при закрытии соединения.
        /// Взводит <see cref="Completion"/>.
        /// </summary>
        /// <param name="possibleReason">Возможная причина обрыва соединения.</param>
        private void AtomicDispose(CloseReason possibleReason)
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            // Только один поток может зайти сюда (за всю жизнь экземпляра).
            // Это настоящая причина обрыва соединения.
            {
                // Лучше выполнить в первую очередь.
                _sendChannel.Writer.TryComplete();

                // Передать исключение всем ожидающим потокам.
                _pendingRequests.PropagateExceptionAndLockup(possibleReason.ToException());

                // Закрыть соединение.
                _socket.Dispose();

                // Синхронизироваться с подписчиками на событие Disconnected.
                EventHandler<SocketDisconnectedEventArgs> disconnected;
                lock (DisconnectEventObj)
                {
                    // Запомнить истинную причину обрыва.
                    DisconnectReason = possibleReason;

                    // Установить флаг после причины обрыва.
                    _isConnected = false;

                    // Скопируем делегат что-бы вызывать не в блокировке — на всякий случай.
                    disconnected = _disconnected;

                    // Теперь можно безопасно убрать подписчиков.
                    _disconnected = null;
                }

                // Установить Task Completion.
                _completionTcs.TrySetResult(possibleReason);

                // Сообщить об обрыве.
                disconnected?.Invoke(this, new SocketDisconnectedEventArgs(possibleReason));
            }
        }
    }
}
