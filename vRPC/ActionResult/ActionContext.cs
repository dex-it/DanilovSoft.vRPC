﻿using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DanilovSoft.vRPC
{
    public sealed class ActionContext
    {
        internal Stream ResponseStream { get; }
        /// <summary>
        /// Не может быть <see langword="null"/>.
        /// </summary>
        internal RequestContext RequestContext { get; }
        public StatusCode StatusCode { get; internal set; }
        internal string ProducesEncoding { get; set; }

        [DebuggerStepThrough]
        internal ActionContext(RequestContext requestContext, Stream responseStream)
        {
            RequestContext = requestContext;
            ResponseStream = responseStream;
        }
    }
}