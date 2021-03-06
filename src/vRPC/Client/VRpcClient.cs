﻿using DanilovSoft.vRPC.Decorator;
using DanilovSoft.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static DanilovSoft.vRPC.ThrowHelper;

namespace DanilovSoft.vRPC
{

    /// <summary>
    /// Контекст клиентского соединения.
    /// </summary>
    [DebuggerDisplay(@"\{{DebugDisplay,nq}\}")]
    public sealed class VRpcClient : IDisposable, IGetProxy
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string DebugDisplay
        {
            get
            {
                var state = State;
                if (state == VRpcState.Open && IsAuthenticated)
                {
                    return $"{state}, Authenticated";
                }
                return state.ToString();
            }
        }
        /// <summary>
        /// Используется для синхронизации установки соединения.
        /// </summary>
        private readonly AsyncLock _connectLock;
        /// <summary>
        /// Адрес для подключения к серверу.
        /// </summary>
        private readonly InvokeActionsDictionary _invokeActions;
        private readonly ProxyCache _proxyCache;
        private readonly ServiceCollection _serviceCollection = new ServiceCollection();
        private bool IsAutoConnectAllowed { get; }
        public Uri ServerAddress { get; private set; }
        /// <summary>
        /// <see langword="volatile"/>.
        /// </summary>
        private ApplicationBuilder? _appBuilder;
        public ServiceProvider? ServiceProvider { get; private set; }
        private Action<ApplicationBuilder>? _configureApp;
        private Func<AccessToken>? _autoAuthentication;

        /// <summary>
        /// Устанавливается в блокировке <see cref="StateLock"/>.
        /// Устанавливается в Null при обрыве соединения.
        /// </summary>
        /// <remarks><see langword="volatile"/></remarks>
        private volatile ClientSideConnection? _connection;
        /// <summary>
        /// Активное соединение. Может быть Null если соединение отсутствует.
        /// </summary>
        public ClientSideConnection? Connection => _connection;

        private Task<CloseReason>? _completion;
        /// <summary>
        /// Завершается если подключение разорвано.
        /// Не бросает исключения.
        /// </summary>
        public Task<CloseReason> Completion => _completion ?? CloseReason.NoConnectionCompletion;
        public VRpcState State
        {
            get
            {
                if (_shutdownRequest != null)
                    return VRpcState.ShutdownRequest;

                return _connection != null ? VRpcState.Open : VRpcState.Closed;
            }
        }
        /// <summary>
        /// Запись через блокировку <see cref="StateLock"/>.
        /// </summary>
        /// <remarks><see langword="volatile"/> служит для публичного доступа.</remarks>
        private volatile ShutdownRequest? _shutdownRequest;
        /// <summary>
        /// Если был начат запрос на остновку, то это свойство будет содержать переданную причину остановки.
        /// Является <see langword="volatile"/>.
        /// </summary>
        public ShutdownRequest? StopRequiredState => _shutdownRequest;
        private bool _disposed;
        /// <summary>
        /// Для доступа к <see cref="_disposed"/> и <see cref="_shutdownRequest"/>.
        /// </summary>
        private object StateLock => _proxyCache;
        /// <summary>
        /// Используется только что-бы аварийно прервать подключение через Dispose.
        /// </summary>
        private ClientWebSocket? _connectingWs;
        /// <summary>
        /// True если соединение прошло аутентификацию на сервере.
        /// </summary>
        public bool IsAuthenticated => _connection?.IsAuthenticated ?? false;
        public event EventHandler<ConnectedEventArgs>? Connected;
        public System.Net.EndPoint? LocalEndPoint => _connection?.LocalEndPoint;
        public System.Net.EndPoint? RemoteEndPoint => _connection?.RemoteEndPoint;

        // ctor.
        static VRpcClient()
        {
            Warmup.DoWarmup();
        }

        // ctor.
        /// <summary>
        /// Создаёт контекст клиентского соединения.
        /// </summary>
        /// <param name="allowAutoConnect">Разрешено ли интерфейсам самостоятельно устанавливать и повторно переподключаться к серверу.</param>
        public VRpcClient(Uri serverAddress, bool allowAutoConnect) 
            : this(Assembly.GetCallingAssembly(), serverAddress, allowAutoConnect)
        {

        }

        // ctor.
        /// <summary>
        /// Создаёт контекст клиентского соединения.
        /// </summary>
        /// <param name="allowAutoConnect">Разрешено ли интерфейсам самостоятельно устанавливать и повторно переподключаться к серверу.</param>
        public VRpcClient(string host, int port, bool ssl, bool allowAutoConnect) 
            : this(Assembly.GetCallingAssembly(), new Uri($"{(ssl ? "wss" : "ws")}://{host}:{port}"), allowAutoConnect)
        {
            
        }

        // ctor.
        /// <summary>
        /// Конструктор клиента.
        /// </summary>
        /// <param name="controllersAssembly">Сборка в которой осуществляется поиск контроллеров.</param>
        /// <param name="serverAddress">Адрес сервера.</param>
        private VRpcClient(Assembly controllersAssembly, Uri serverAddress, bool allowAutoConnect)
        {
            Debug.Assert(controllersAssembly != Assembly.GetExecutingAssembly());

            // Найти все контроллеры в вызывающей сборке.
            Dictionary<string, Type> controllerTypes = GlobalVars.FindAllControllers(controllersAssembly);

            // Словарь с методами контроллеров.
            _invokeActions = new InvokeActionsDictionary(controllerTypes);
            ServerAddress = serverAddress;
            _connectLock = new AsyncLock();
            IsAutoConnectAllowed = allowAutoConnect;
            _proxyCache = new ProxyCache();

            InnerConfigureIoC(controllerTypes.Values);
        }

        /// <summary>
        /// Позволяет настроить IoC контейнер.
        /// Выполняется единожды при инициализации подключения.
        /// </summary>
        /// <exception cref="VRpcException"/>
        /// <exception cref="ArgumentNullException"/>
        /// <exception cref="ObjectDisposedException"/>
        public void ConfigureService(Action<ServiceCollection> configure)
        {
            if (configure == null)
                ThrowHelper.ThrowArgumentNullException(nameof(configure));

            ThrowIfDisposed();

            if (ServiceProvider != null)
                ThrowHelper.ThrowVRpcException("Service already configured.");

            configure(_serviceCollection);
            ServiceProvider = _serviceCollection.BuildServiceProvider();
        }

        /// <exception cref="VRpcException"/>
        /// <exception cref="ObjectDisposedException"/>
        public void Configure(Action<ApplicationBuilder> configureApp)
        {
            if (configureApp == null)
                ThrowHelper.ThrowArgumentNullException(nameof(configureApp));

            ThrowIfDisposed();

            if (_configureApp != null)
                ThrowHelper.ThrowVRpcException("RpcClient already configured.");

            _configureApp = configureApp;
        }

        /// <exception cref="VRpcException"/>
        /// <exception cref="ObjectDisposedException"/>
        public void ConfigureAutoAuthentication(Func<AccessToken> configure)
        {
            if (configure == null)
                ThrowHelper.ThrowArgumentNullException(nameof(configure));

            ThrowIfDisposed();

            if (_autoAuthentication != null)
                ThrowHelper.ThrowVRpcException("Auto authentication already configured.");

            _autoAuthentication = configure;
        }

        /// <summary>
        /// Блокирует поток до завершения <see cref="Completion"/>.
        /// </summary>
        public CloseReason WaitCompletion()
        {
            return Completion.GetAwaiter().GetResult();
        }

        #region Public Connect

        /// <summary>
        /// Производит предварительное подключение к серверу. Может использоваться для повторного подключения.
        /// </summary>
        /// <remarks>Потокобезопасно.</remarks>
        /// <exception cref="VRpcConnectException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="ObjectDisposedException"/>
        public void Connect()
        {
            ConnectAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Производит предварительное подключение к серверу. Может использоваться для повторного переподключения.
        /// </summary>
        /// <remarks>Потокобезопасно.</remarks>
        /// <exception cref="VRpcConnectException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="ObjectDisposedException"/>
        public async Task ConnectAsync()
        {
            ConnectResult connectResult = await ConnectExAsync().ConfigureAwait(false);
            
            switch (connectResult.State)
            {
                case ConnectionState.Connected:
                    return;
                case ConnectionState.SocketError:
                    {
                        Debug.Assert(connectResult.SocketError != null);

                        ThrowHelper.ThrowConnectException(
                            message: $"Unable to connect to the remote server. Error: {(int)connectResult.SocketError}",
                            innerException: connectResult.SocketError.Value.ToException());

                        break;
                    }
                case ConnectionState.ShutdownRequest:
                    {
                        Debug.Assert(connectResult.ShutdownRequest != null);
                        ThrowHelper.ThrowException(connectResult.ShutdownRequest.ToException());
                        break;
                    }
            }
        }

        /// <summary>
        /// Производит предварительное подключение к серверу. Может использоваться для повторного переподключения.
        /// Помимо кода возврата может бросить исключение типа <see cref="VRpcConnectException"/>.
        /// </summary>
        /// <remarks>Потокобезопасно.</remarks>
        /// <exception cref="VRpcConnectException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="ObjectDisposedException"/>
        public ConnectResult ConnectEx()
        {
            return ConnectExAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Производит предварительное подключение к серверу. Может использоваться для повторного переподключения.
        /// Помимо кода возврата может бросить исключение типа <see cref="VRpcConnectException"/>.
        /// </summary>
        /// <remarks>Потокобезопасно.</remarks>
        /// <exception cref="VRpcConnectException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="ObjectDisposedException"/>
        public Task<ConnectResult> ConnectExAsync()
        {
            ThrowIfDisposed();
            ThrowIfWasShutdown();

            ValueTask<InnerConnectionResult> t = ConnectOrGetExistedConnectionAsync(default);
            if(t.IsCompletedSuccessfully)
            {
                InnerConnectionResult conRes = t.Result;
                return Task.FromResult(conRes.ToPublicConnectResult());
            }
            else
            {
                return WaitForConnectAsync(t);
            }

            // Локальная.
            static async Task<ConnectResult> WaitForConnectAsync(ValueTask<InnerConnectionResult> t)
            {
                InnerConnectionResult conRes;
                try
                {
                    conRes = await t.ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    ThrowHelper.ThrowConnectException($"Unable to connect to the remote server. ErrorCode: {ex.ErrorCode}", ex);
                    return default;
                }
                catch (System.Net.WebSockets.WebSocketException ex)
                {
                    ThrowHelper.ThrowConnectException($"Unable to connect to the remote server. ErrorCode: {ex.ErrorCode}", ex);
                    return default;
                }
                catch (HttpHandshakeException ex)
                {
                    ThrowHelper.ThrowConnectException($"Unable to connect to the remote server due to handshake error", ex);
                    return default;
                }
                return conRes.ToPublicConnectResult();
            }
        }
        #endregion

        /// <summary>
        /// Выполняет аутентификацию текущего соединения.
        /// </summary>
        /// <param name="accessToken">Аутентификационный токен передаваемый серверу.</param>
        /// <exception cref="VRpcConnectionNotOpenException"/>
        public void SignIn(AccessToken accessToken)
        {
            SignInAsync(accessToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Выполняет аутентификацию текущего соединения.
        /// </summary>
        /// <param name="accessToken">Аутентификационный токен передаваемый серверу.</param>
        /// <exception cref="VRpcConnectionNotOpenException"/>
        public Task SignInAsync(AccessToken accessToken)
        {
            accessToken.ValidateAccessToken(nameof(accessToken));
            ThrowIfDisposed();
            ThrowIfWasShutdown();

            // Начать соединение или взять существующее.
            ValueTask<ClientSideConnection> connectionTask = GetOrOpenConnection(accessToken);

            if (connectionTask.IsCompleted)
            {
                // Может бросить исключение.
                ClientSideConnection connection = connectionTask.Result;

                return connection.SignInAsync(accessToken);
            }
            else
            {
                return WaitConnection(connectionTask, accessToken);
            }

            static async Task WaitConnection(ValueTask<ClientSideConnection> t, AccessToken accessToken)
            {
                ClientSideConnection connection = await t.ConfigureAwait(false);
                await connection.SignInAsync(accessToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="VRpcConnectionNotOpenException"/>
        public void SignOut()
        {
            SignOutAsync().GetAwaiter().GetResult();
        }

        /// <exception cref="ObjectDisposedException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="VRpcConnectionNotOpenException"/>
        public Task SignOutAsync()
        {
            ThrowIfDisposed();
            ThrowIfWasShutdown();

            // Копия volatile.
            ClientSideConnection? connection = _connection;

            if (connection != null)
            {
                return connection.SignOutAsync();  
            }
            else
            // Соединение закрыто и технически можно считать что операция выполнена успешно.
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Создает прокси к методам удалённой стороны на основе интерфейса. Повторное обращение вернет экземпляр из кэша.
        /// Полученный экземпляр можно привести к типу <see cref="ClientInterfaceProxy"/>.
        /// Метод является шорткатом для <see cref="GetProxyDecorator"/>
        /// </summary>
        /// <typeparam name="T">Интерфейс.</typeparam>
        public T GetProxy<T>() where T : class
        {
            var proxy = GetProxyDecorator<T>().Proxy;
            Debug.Assert(proxy != null);
            return proxy;
        }

        /// <summary>
        /// Создает прокси к методам удалённой стороны на основе интерфейса. Повторное обращение вернет экземпляр из кэша.
        /// </summary>
        /// <typeparam name="T">Интерфейс.</typeparam>
        public ClientInterfaceProxy<T> GetProxyDecorator<T>() where T : class
        {
            var decorator = _proxyCache.GetProxyDecorator<T>(this);
            return decorator;
        }

        // Когда выполняют вызов метода через интерфейс.
        internal Task<T> OnClientMethodCall<T>(RequestMethodMeta methodMeta, object[] args)
        {
            Debug.Assert(!methodMeta.IsNotificationRequest);

            // Начать соединение или взять существующее.
            ValueTask<ClientSideConnection> connectionTask = GetOrOpenConnection(default);

            return ManagedConnection.OnClientMethodCall<T>(connectionTask, methodMeta, args);
        }

        // Когда выполняют вызов метода через интерфейс.
        internal ValueTask OnClientNotificationCall(RequestMethodMeta methodMeta, object[] args)
        {
            // Начать соединение или взять существующее.
            ValueTask<ClientSideConnection> connectionTask = GetOrOpenConnection(default);

            return ManagedConnection.OnClientNotificationCall(connectionTask, methodMeta, args);
        }

        /// <summary>
        /// Выполняет грациозную остановку. Блокирует поток не дольше чем задано в <paramref name="disconnectTimeout"/>.
        /// Потокобезопасно.
        /// </summary>
        /// <param name="disconnectTimeout">Максимальное время ожидания завершения выполняющихся запросов.</param>
        /// <param name="closeDescription">Причина закрытия соединения которая будет передана удалённой стороне.</param>
        public CloseReason Shutdown(TimeSpan disconnectTimeout, string? closeDescription = null)
        {
            return ShutdownAsync(disconnectTimeout, closeDescription).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Начинает грациозную остановку. Не блокирует поток.
        /// Результат остановки можно получить через <see cref="Completion"/>.
        /// Потокобезопасно.
        /// </summary>
        /// <param name="disconnectTimeout">Максимальное время ожидания завершения выполняющихся запросов.</param>
        /// <param name="closeDescription">Причина закрытия соединения которая будет передана удалённой стороне.</param>
        public void BeginShutdown(TimeSpan disconnectTimeout, string? closeDescription = null)
        {
            _ = PrivateShutdownAsync(disconnectTimeout, closeDescription);
        }

        /// <summary>
        /// Выполняет грациозную остановку. Блокирует выполнение не дольше чем задано в <paramref name="disconnectTimeout"/>.
        /// Потокобезопасно.
        /// </summary>
        /// <param name="disconnectTimeout">Максимальное время ожидания завершения выполняющихся запросов.</param>
        /// <param name="closeDescription">Причина закрытия соединения которая будет передана удалённой стороне.</param>
        public Task<CloseReason> ShutdownAsync(TimeSpan disconnectTimeout, string? closeDescription = null)
        {
            return PrivateShutdownAsync(disconnectTimeout, closeDescription);
        }

        private async Task<CloseReason> PrivateShutdownAsync(TimeSpan disconnectTimeout, string? closeDescription)
        {
            bool created;
            ShutdownRequest? stopRequired;
            ClientSideConnection? connection;
            lock (StateLock)
            {
                stopRequired = _shutdownRequest;
                if (stopRequired == null)
                {
                    stopRequired = new ShutdownRequest(disconnectTimeout, closeDescription);

                    // Волатильно взводим флаг о необходимости остановки.
                    _shutdownRequest = stopRequired;
                    created = true;

                    // Прервать установку подключения если она выполняется.
                    Interlocked.Exchange(ref _connectingWs, null)?.Dispose();

                    // Скопировать пока мы в блокировке.
                    connection = _connection;
                }
                else
                {
                    created = false;
                    connection = null;
                }
            }

            CloseReason closeReason;

            if (created)
            // Только один поток зайдёт сюда.
            {
                if (connection != null)
                // Существует живое соединение.
                {
                    closeReason = await connection.InnerShutdownAsync(stopRequired).ConfigureAwait(false);
                }
                else
                // Соединения не существует и новые создаваться не смогут.
                {
                    closeReason = CloseReason.NoConnectionGracifully;

                    // Передать результат другим потокам которые повторно вызовут Shutdown.
                    stopRequired.SetTaskResult(closeReason);
                }
            }
            else
            // Другой поток уже начал остановку.
            {
                closeReason = await stopRequired.Task.ConfigureAwait(false);
            }

            return closeReason;
        }

        /// <summary>
        /// Возвращает существующее подключение или создаёт новое если это разрешает свойство <see cref="IsAutoConnectAllowed"/>.
        /// </summary>
        /// <exception cref="SocketException"/>
        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="VRpcConnectionNotOpenException"/>
        internal ValueTask<ClientSideConnection> GetOrOpenConnection(AccessToken accessToken)
        {
            // Копия volatile.
            ClientSideConnection? connection = _connection;

            if (connection != null)
            // Есть живое соединение.
            {
                //createdNew = false;
                return new ValueTask<ClientSideConnection>(connection);
            }
            else
            // Нужно установить подключение.
            {
                if (IsAutoConnectAllowed)
                {
                    if (!TryGetShutdownException(out ValueTask<ClientSideConnection> shutdownException))
                    {
                        ValueTask<InnerConnectionResult> t = ConnectOrGetExistedConnectionAsync(accessToken);
                        if (t.IsCompletedSuccessfully)
                        {
                            InnerConnectionResult connectionResult = t.Result; // Взять успешный результат.
                            return connectionResult.ToManagedConnectionTask();
                        }
                        else
                        {
                            return WaitForConnectionAsync(t);
                        }
                    }
                    else
                    // Уже был вызван Shutdown.
                    {
                        return shutdownException;
                    }
                }
                else
                {
                    return new ValueTask<ClientSideConnection>(Task.FromException<ClientSideConnection>(new VRpcConnectionNotOpenException()));
                }
            }

            // Локальная.
            static async ValueTask<ClientSideConnection> WaitForConnectionAsync(ValueTask<InnerConnectionResult> t)
            {
                InnerConnectionResult connectionResult = await t.ConfigureAwait(false);
                return connectionResult.ToManagedConnection();
            }
        }

        /// <summary>
        /// Событие — обрыв сокета. Потокобезопасно. Срабатывает только один раз.
        /// </summary>
        private void OnDisconnected(object? sender, SocketDisconnectedEventArgs e)
        {
            // volatile.
            _connection = null;

            // Отпишем отключенный экземпляр.
            e.Connection.Disconnected -= OnDisconnected;
        }

        /// <summary>
        /// Выполнить подключение сокета если еще не подключен.
        /// </summary>
        private ValueTask<InnerConnectionResult> ConnectOrGetExistedConnectionAsync(AccessToken accessToken)
        {
            // Копия volatile.
            ClientSideConnection? connection = _connection;

            if (connection != null)
            // Есть живое соединение.
            {
                return new ValueTask<InnerConnectionResult>(InnerConnectionResult.FromExistingConnection(connection));
            }
            else
            // Подключение отсутствует.
            {
                // Захватить блокировку.
                ValueTask<AsyncLock.Releaser> t = _connectLock.LockAsync();
                if (t.IsCompletedSuccessfully)
                {
                    AsyncLock.Releaser releaser = t.Result;
                    return LockAquiredConnectAsync(releaser, accessToken);
                }
                else
                {
                    return WaitForLockAndConnectAsync(t, accessToken);
                }
            }

            async ValueTask<InnerConnectionResult> WaitForLockAndConnectAsync(ValueTask<AsyncLock.Releaser> t, AccessToken accessToken)
            {
                AsyncLock.Releaser releaser = await t.ConfigureAwait(false);
                return await LockAquiredConnectAsync(releaser, accessToken).ConfigureAwait(false);
            }
        }

        /// <exception cref="VRpcWasShutdownException"/>
        /// <exception cref="ObjectDisposedException"/>
        private async ValueTask<InnerConnectionResult> LockAquiredConnectAsync(AsyncLock.Releaser conLock, AccessToken accessToken)
        {
            InnerConnectionResult conResult;
            using (conLock)
            {
                conResult = await LockAquiredConnectAsync(accessToken).ConfigureAwait(false);
            }

            // Только один поток получит соединение с этим флагом.
            if (conResult.NewConnectionCreated)
            {
                Connected?.Invoke(this, new ConnectedEventArgs(conResult.Connection));
            }

            return conResult;
        }

        private async ValueTask<InnerConnectionResult> LockAquiredConnectAsync(AccessToken accessToken)
        {
            // Копия volatile.
            ClientSideConnection? connection = _connection;

            if (connection == null)
            {
                ServiceProvider? serviceProvider = ServiceProvider;
                lock (StateLock)
                {
                    if (!_disposed)
                    {
                        // Пока в блокировке можно безопасно трогать свойство _shutdownRequest.
                        if (_shutdownRequest != null)
                        {
                            // Нельзя создавать новое подключение если был вызван Stop.
                            return InnerConnectionResult.FromShutdownRequest(_shutdownRequest);
                        }
                        else
                        {
                            if (serviceProvider == null)
                            {
                                serviceProvider = _serviceCollection.BuildServiceProvider();
                                ServiceProvider = serviceProvider;
                            }
                        }
                    }
                    else
                    {
                        ThrowHelper.ThrowObjectDisposedException(GetType().FullName);
                    }
                }

                _appBuilder = new ApplicationBuilder(serviceProvider);
                _configureApp?.Invoke(_appBuilder);

                // Новый сокет.
                var ws = new ClientWebSocket();
                ClientWebSocket? toDispose = ws;

                ws.Options.KeepAliveInterval = _appBuilder.KeepAliveInterval;
                ws.Options.ReceiveTimeout = _appBuilder.ReceiveTimeout;

                // Позволить Dispose прервать подключение.
                Interlocked.Exchange(ref _connectingWs, ws);

                try
                {
                    // Обычное подключение Tcp.
                    ReceiveResult wsReceiveResult = await ws.ConnectExAsync(ServerAddress, CancellationToken.None).ConfigureAwait(false);

                    if (Interlocked.Exchange(ref _connectingWs, null) == null)
                    // Другой поток уничтожил наш web-socket.
                    {
                        // Предотвратим лишний Dispose.
                        toDispose = null;

                        lock (StateLock)
                        {
                            if (!_disposed)
                            {
                                if (_shutdownRequest != null)
                                // Другой поток вызвал Shutdown.
                                {
                                    return InnerConnectionResult.FromShutdownRequest(_shutdownRequest);
                                }
                            }
                            else
                            // Другой поток вызвал Dispose.
                            {
                                // Больше ничего делать не нужно.
                                ThrowHelper.ThrowObjectDisposedException(GetType().FullName);
                            }
                        }
                    }

                    if (wsReceiveResult.IsReceivedSuccessfully)
                    // Соединение успешно установлено.
                    {
                        ShutdownRequest? stopRequired = null;
                        lock (StateLock)
                        {
                            if (!_disposed)
                            {
                                connection = new ClientSideConnection(this, ws, serviceProvider, _invokeActions);

                                // Предотвратить Dispose.
                                toDispose = null;

                                // Скопировать пока мы в блокировке.
                                stopRequired = _shutdownRequest;

                                if (stopRequired == null)
                                {
                                    // Скопируем таск соединения.
                                    _completion = connection.Completion;

                                    // Косвенно устанавливает флаг IsConnected.
                                    _connection = connection;
                                }
                            }
                            else
                            // Был выполнен Dispose в тот момент когда велась попытка установить соединение.
                            {
                                ThrowHelper.ThrowObjectDisposedException(GetType().FullName);
                            }
                        }

                        if (stopRequired == null)
                        // Запроса на остановку сервиса ещё не было.
                        {
                            connection.StartReceiveLoopThreads();
                            connection.Disconnected += OnDisconnected;

                            if (accessToken == (default))
                            {
                                // Запросить токен у пользователя.
                                AccessToken autoAccessToken = _autoAuthentication?.Invoke() ?? default;
                                if (accessToken != default)
                                {
                                    await connection.PrivateSignInAsync(accessToken).ConfigureAwait(false);
                                }
                            }
                            else
                            // Приоритет у токена переданный как параметр — это явная SignIn операция.
                            {
                                await connection.PrivateSignInAsync(accessToken).ConfigureAwait(false);
                            }

                            // Успешно подключились.
                            return InnerConnectionResult.FromNewConnection(connection);
                        }
                        else
                        // Был запрос на остановку сервиса. 
                        // Он произошел в тот момент когда велась попытка установить соединение.
                        // Это очень редкий случай но мы должны его предусмотреть.
                        {
                            using (connection)
                            {
                                // Мы обязаны закрыть это соединение.
                                await connection.InnerShutdownAsync(stopRequired).ConfigureAwait(false);
                            }

                            return InnerConnectionResult.FromShutdownRequest(stopRequired);
                        }
                    }
                    else
                    // Подключение не удалось.
                    {
                        return InnerConnectionResult.FromConnectionError(wsReceiveResult.SocketError);
                    }
                }
                finally
                {
                    toDispose?.Dispose();
                }
            }
            else
            // Подключать сокет не нужно — есть живое соединение.
            {
                return InnerConnectionResult.FromExistingConnection(connection);
            }
        }

        /// <summary>
        /// Добавляет в IoC контейнер контроллеры из сборки и компилирует контейнер.
        /// </summary>
        private void InnerConfigureIoC(IEnumerable<Type> controllers)
        {
            // Добавим в IoC все контроллеры сборки.
            foreach (Type controllerType in controllers)
                _serviceCollection.AddScoped(controllerType);

            _serviceCollection.AddScoped<RequestContextScope>();
            _serviceCollection.AddScoped(typeof(IProxy<>), typeof(ProxyFactory<>));

            //ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
            //return serviceProvider;
        }

        /// <exception cref="ObjectDisposedException"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (!_disposed)
            {
                return;
            }
            else
            {
                ThrowHelper.ThrowObjectDisposedException(GetType().FullName);
            }
        }

        /// <summary>
        /// Проверяет установку волатильного свойства <see cref="_shutdownRequest"/>.
        /// </summary>
        /// <exception cref="VRpcWasShutdownException"/>
        private void ThrowIfWasShutdown()
        {
            // volatile копия.
            ShutdownRequest? shutdownRequired = _shutdownRequest;

            if (shutdownRequired == null)
            {
                return;
            }
            else
            // В этом экземпляре уже был запрос на остановку.
            {
                ThrowHelper.ThrowWasShutdownException(shutdownRequired);
            }
        }

        /// <summary>
        /// Проверяет установку волатильного свойства <see cref="_shutdownRequest"/>.
        /// </summary>
        private bool TryGetShutdownException<T>(out ValueTask<T> exceptionTask)
        {
            // volatile копия.
            ShutdownRequest? stopRequired = _shutdownRequest;

            if (stopRequired == null)
            {
                exceptionTask = default;
                return false;
            }
            else
            // В этом экземпляре уже был запрос на остановку.
            {
                exceptionTask = new ValueTask<T>(Task.FromException<T>(new VRpcWasShutdownException(stopRequired)));
                return true;
            }
        }

        public void Dispose()
        {
            lock (StateLock)
            {
                if (!_disposed)
                {
                    _disposed = true;

                    // Прервать установку подключения если она выполняется.
                    Interlocked.Exchange(ref _connectingWs, null)?.Dispose();

                    _connection?.Dispose();
                    ServiceProvider?.Dispose();
                    Connected = null;
                }
            }
        }
    }
}
