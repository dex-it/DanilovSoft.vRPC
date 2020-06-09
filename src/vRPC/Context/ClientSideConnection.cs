﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DanilovSoft.WebSockets;
using Microsoft.Extensions.DependencyInjection;

namespace DanilovSoft.vRPC
{
    [DebuggerDisplay(@"\{IsConnected = {IsConnected}\}")]
    public sealed class ClientSideConnection : ManagedConnection
    {
        /// <summary>
        /// Internal запрос для аутентификации.
        /// </summary>
        private static readonly RequestMethodMeta SignInAsyncMeta = new RequestMethodMeta("", "SignIn", returnType: typeof(Task), notification: false);
        private static readonly RequestMethodMeta SignOutAsyncMeta = new RequestMethodMeta("", "SignOut", returnType: typeof(Task), notification: false);
        //internal static readonly ServerConcurrentDictionary<MethodInfo, RequestMeta> InterfaceMethodsInfo = new ServerConcurrentDictionary<MethodInfo, RequestMeta>();
        internal static readonly LockedDictionary<MethodInfo, RequestMethodMeta> InterfaceMethodsInfo = new LockedDictionary<MethodInfo, RequestMethodMeta>();
        /// <summary>
        /// Методы SignIn, SignOut (async) должны выполняться последовательно
        /// что-бы синхронизироваться со свойством IsAuthenticated.
        /// </summary>
        private readonly object _authLock = new object();
        public RpcClient Client { get; }
        /// <summary>
        /// Установка свойства только через блокировку <see cref="_authLock"/>.
        /// Перед чтением этого значения нужно дождаться завершения <see cref="_lastAuthTask"/> — этот таск может модифицировать значение минуя захват блокировки.
        /// </summary>
        private volatile bool _isAuthenticated;
        public override bool IsAuthenticated => _isAuthenticated;
        /// <summary>
        /// Установка свойства только через блокировку <see cref="_authLock"/>.
        /// Этот таск настроен не провоцировать исключения.
        /// </summary>
        private Task _lastAuthTask = Task.CompletedTask;
        private protected override IConcurrentDictionary<MethodInfo, RequestMethodMeta> InterfaceMethods => InterfaceMethodsInfo;

        // ctor.
        /// <summary>
        /// Принимает открытое соединение Web-Socket.
        /// </summary>
        internal ClientSideConnection(RpcClient client, ClientWebSocket ws, ServiceProvider serviceProvider, InvokeActionsDictionary controllers)
            : base(ws.ManagedWebSocket, isServer: false, serviceProvider, controllers)
        {
            Client = client;
        }

        // Клиент всегда разрешает серверу вызывать свои методы.
        private protected override bool ActionPermissionCheck(ControllerActionMeta actionMeta, out IActionResult? permissionError, out ClaimsPrincipal? user)
        {
            user = null;
            permissionError = null;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private protected override T InnerGetProxy<T>()
        {
            return Client.GetProxy<T>();
        }

        /// <summary>
        /// Выполняет аутентификацию соединения.
        /// </summary>
        /// <param name="accessToken">Аутентификационный токен передаваемый серверу.</param>
        internal void SignIn(AccessToken accessToken)
        {
            SignInAsync(accessToken).GetAwaiter().GetResult();
            //lock (_authLock)
            //{
            //    if (!_lastAuthTask.IsCompleted)
            //    // Кто-то уже выполняет SignIn/Out — нужно дождаться завершения.
            //    {
            //        // ВНИМАНИЕ опасность дедлока — _lastAuthTask не должен делать lock.
            //        // Не бросает исключения.
            //        _lastAuthTask.GetAwaiter().GetResult();
            //    }
                
            //    // Создаём запрос для отправки.
            //    BinaryMessageToSend binaryRequest = SignInMeta.SerializeRequest(new object[] { accessToken });
            //    try
            //    {
            //        var requestResult = SendRequestAndGetResult(binaryRequest, SignInMeta);
            //        binaryRequest = null;
            //        Debug.Assert(requestResult == null);
            //    }
            //    finally
            //    {
            //        binaryRequest?.Dispose();
            //    }

            //    _isAuthenticated = true;
            //}
        }

        /// <summary>
        /// Выполняет аутентификацию соединения.
        /// </summary>
        /// <param name="accessToken">Аутентификационный токен передаваемый серверу.</param>
        internal async Task SignInAsync(AccessToken accessToken)
        {
            bool retryRequired;
            do
            {
                Task task;
                lock (_authLock)
                {
                    if (_lastAuthTask.IsCompleted)
                    // Теперь мы имеем эксклюзивную возможность выполнить SignIn/Out.
                    {
                        // Начали свой запрос.
                        task = PrivateSignInAsync(accessToken);

                        // Можем обновить свойство пока в блокировке.
                        _lastAuthTask = task.ContinueWith(t => { }, default, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                        // Повторять захват блокировки больше не нужно.
                        retryRequired = false;
                    }
                    else
                    // Кто-то уже выполняет SignIn/Out.
                    {
                        // Будем ожидать чужой таск.
                        task = _lastAuthTask;

                        // Нужно повторить захват блокировки после завершения таска.
                        retryRequired = true;
                    }
                }

                // Наш таск может бросить исключение — чужой не может бросить исключение.
                await task.ConfigureAwait(false);

            } while (retryRequired);
        }

        internal async Task PrivateSignInAsync(AccessToken accessToken)
        {
            Task pendingRequestTask;

            // Создаём запрос для отправки.
            SerializedMessageToSend serializedMessage = SignInAsyncMeta.SerializeRequest(new object[] { accessToken });
            SerializedMessageToSend? toDispose = serializedMessage;

            try
            {
                pendingRequestTask = SendRequestAndWaitResponse(SignInAsyncMeta, serializedMessage);
                toDispose = null;
            }
            finally
            {
                toDispose?.Dispose();
            }

            // Ждём завершения SignIn.
            await pendingRequestTask.ConfigureAwait(false);

            // Делать lock нельзя! Может случиться дедлок (а нам и не нужно).
            _isAuthenticated = true;
        }

        /// <summary>
        /// 
        /// </summary>
        internal void SignOut()
        {
            SignOutAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 
        /// </summary>
        internal async Task SignOutAsync()
        {
            bool retryRequired;
            do
            {
                Task task;
                lock (_authLock)
                {
                    if (_lastAuthTask.IsCompleted)
                    // Теперь мы имеем эксклюзивную возможность выполнить SignIn/Out.
                    {
                        if (_isAuthenticated)
                        {
                            // Начали свой запрос.
                            task = PrivateSignOutAsync();

                            // Можем обновить свойство пока в блокировке.
                            _lastAuthTask = task.ContinueWith(t => { }, default, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                            // Повторять захват блокировки больше не нужно.
                            retryRequired = false;
                        }
                        else
                        // Другой поток уже выполнил SignOut — отправлять очередной запрос бессмысленно.
                        {
                            return;
                        }
                    }
                    else
                    // Кто-то уже выполняет SignIn/Out.
                    {
                        // Будем ожидать чужой таск.
                        task = _lastAuthTask;

                        // Нужно повторить захват блокировки после завершения таска.
                        retryRequired = true;
                    }
                }

                // Наш таск может бросить исключение — чужой не может бросить исключение.
                await task.ConfigureAwait(false);

            } while (retryRequired);
        }

        private async Task PrivateSignOutAsync()
        {
            Task pendingRequestTask;

            // Создаём запрос для отправки.
            SerializedMessageToSend binaryRequest = SignOutAsyncMeta.SerializeRequest(Array.Empty<object>());
            SerializedMessageToSend? toDispose = binaryRequest;

            try
            {
                pendingRequestTask = SendRequestAndWaitResponse(SignOutAsyncMeta, binaryRequest);
                toDispose = null;
            }
            finally
            {
                toDispose?.Dispose();
            }

            // Ждём завершения SignOut — исключений быть не может, только при обрыве связи.
            await pendingRequestTask.ConfigureAwait(false);

            // Делать lock нельзя! Может случиться дедлок (а нам и не нужно).
            _isAuthenticated = false;
        }
    }
}