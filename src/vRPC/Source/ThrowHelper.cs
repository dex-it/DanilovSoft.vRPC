﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DanilovSoft.vRPC
{
    /// <summary>
    /// Для оптимизации горячих путей. Потому что JIT не встраивает методы в которых есть throw.
    /// </summary>
    internal static class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowException(Exception exception) => throw exception;

        /// <exception cref="VRpcConnectException"/>
        [DoesNotReturn]
        internal static void ThrowConnectException(string message, Exception innerException) =>
            throw new VRpcConnectException(message, innerException);

        /// <exception cref="ObjectDisposedException"/>
        [DoesNotReturn]
        internal static void ThrowObjectDisposedException(string? objectName) =>
            throw new ObjectDisposedException(objectName);

        /// <exception cref="ObjectDisposedException"/>
        [DoesNotReturn]
        internal static void ThrowObjectDisposedException(string? objectName, string? message) =>
            throw new ObjectDisposedException(objectName, message);

        /// <exception cref="VRpcWasShutdownException"/>
        [DoesNotReturn]
        internal static void ThrowWasShutdownException(ShutdownRequest shutdownRequired) =>
            throw new VRpcWasShutdownException(shutdownRequired);

        /// <exception cref="ArgumentNullException"/>
        [DoesNotReturn]
        internal static void ThrowArgumentNullException(string? paramName) =>
            throw new ArgumentNullException(paramName);

        /// <exception cref="VRpcException"/>
        [DoesNotReturn]
        internal static void ThrowVRpcException(string message) =>
            throw new VRpcException(message);

        /// <exception cref="VRpcException"/>
        [DoesNotReturn]
        internal static void ThrowVRpcException(string message, Exception innerException) =>
            throw new VRpcException(message, innerException);

        /// <exception cref="ArgumentOutOfRangeException"/>
        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException(string? paramName) =>
            throw new ArgumentOutOfRangeException(paramName);

        /// <exception cref="ArgumentException"/>
        [DoesNotReturn]
        internal static void ThrowArgumentException(string? message) =>
            throw new ArgumentException(message);

        /// <exception cref="ArgumentOutOfRangeException"/>
        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException(string message, string paramName) =>
            throw new ArgumentOutOfRangeException(paramName, message);

        /// <exception cref="InvalidOperationException"/>
        [DoesNotReturn]
        internal static void ThrowInvalidOperationException(string? message) => throw new InvalidOperationException(message);
    }
}
