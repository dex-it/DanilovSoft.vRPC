﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DanilovSoft.vRPC
{
    public sealed class SocketDisconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Причина обрыва соединения.
        /// </summary>
        public CloseReason DisconnectReason { get; }

        [DebuggerStepThrough]
        public SocketDisconnectedEventArgs(CloseReason closeResult)
        {
            DisconnectReason = closeResult;
        }
    }
}
