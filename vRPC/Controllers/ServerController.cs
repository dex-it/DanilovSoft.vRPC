﻿using Microsoft.Extensions.DependencyInjection;

namespace DanilovSoft.vRPC
{
    public abstract class ServerController : Controller
    {
        /// <summary>
        /// Контекст подключения на стороне сервера.
        /// </summary>
        public ServerSideConnection Context { get; internal set; }
        //public ServiceProvider ServiceProvider => ServiceProvider;

        ///// <summary>
        ///// Шорткат для Context.UserId.Value.
        ///// </summary>
        //public int UserId => Context.UserId.Value;

        // ctor.
        public ServerController()
        {

        }
    }
}
