﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DanilovSoft.vRPC.Decorator
{
    /// <summary>
    /// Этот клас наследует пользовательские интерфейсы.
    /// </summary>
    public abstract class ServerInterfaceProxy
    {
        public string ControllerName { get; protected set; }
        public ManagedConnection Connection { get; protected set; }
    }

    // Тип должен быть публичным и не запечатанным.
    /// <summary>
    /// Этот клас наследует пользовательские интерфейсы.
    /// </summary>
    [DebuggerDisplay(@"\{Proxy to remote controller {ControllerName}, {Connection}\}")]
    public class ServerInterfaceProxy<TIface> : ServerInterfaceProxy, IInterfaceProxy, IInterfaceDecorator<TIface> where TIface : class
    {
        public TIface Proxy { get; private set; }

        // Вызывается через рефлексию.
        public ServerInterfaceProxy()
        {
            // Этот конструктор является базовым для динамически созданного наследника.
        }

        internal void InitializeClone(string controllerName, ManagedConnection connection)
        {
            Proxy = this as TIface;
            ControllerName = controllerName;
            Connection = connection;
        }

        T IInterfaceProxy.Clone<T>()
        {
            return MemberwiseClone() as T;
        }

        // Вызывается через рефлексию.
        protected object Invoke(MethodInfo targetMethod, object[] args)
        {
            return Connection.OnInterfaceMethodCall(targetMethod, args, ControllerName);
        }
    }
}
