﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text;

namespace DanilovSoft.vRPC
{
    /// <summary>
    /// Происходит при обращении к выключенному экземпляру или находящемуся в процессе отключения по запросу пользователя.
    /// </summary>
    [Serializable]
    public sealed class VRpcWasShutdownException : VRpcException
    {
        public ShutdownRequest? StopRequiredState { get; }

        public VRpcWasShutdownException() { }

        public VRpcWasShutdownException(string message) : base(message) { }

        public VRpcWasShutdownException(string message, Exception innerException) : base(message, innerException) { }

        internal VRpcWasShutdownException(ShutdownRequest stopRequired) : base(CreateExceptionMessage(stopRequired))
        {
            Debug.Assert(stopRequired != null);
            StopRequiredState = stopRequired;
        }

        private static string CreateExceptionMessage(ShutdownRequest stopRequired)
        {
            if (!string.IsNullOrEmpty(stopRequired.CloseDescription))
            {
                return $"Использовать этот экземпляр больше нельзя — был вызван " +
                    $"Shutdown (DisconnectTimeout: {stopRequired.ShutdownTimeout}) со следующим объяснением причины: '{stopRequired.CloseDescription}'.";
            }
            else
            {
                return $"Использовать этот экземпляр больше нельзя — был вызван " +
                    $"Shutdown (DisconnectTimeout: {stopRequired.ShutdownTimeout}) без дополнительного объяснения причины.";
            }
        }

#pragma warning disable CA1801
        private VRpcWasShutdownException(SerializationInfo serializationInfo, StreamingContext streamingContext)
        {
            
        }
#pragma warning restore CA1801
    }
}
