﻿using DanilovSoft.vRPC.Source;
using ProtoBuf;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProtoBufSerializer = ProtoBuf.Serializer;

namespace DanilovSoft.vRPC
{
    /// <summary>
    /// Заголовок запроса или ответа. Бинарный размер — динамический. Сериализуется всегда через ProtoBuf.
    /// </summary>
    [ProtoContract]
    [StructLayout(LayoutKind.Auto)]
    [DebuggerDisplay("{DebugDisplay,nq}")]
    internal readonly struct HeaderDto : IEquatable<HeaderDto>
    {
        private static readonly JsonEncodedText JsonUid = JsonEncodedText.Encode("uid");
        private static readonly JsonEncodedText JsonCode = JsonEncodedText.Encode("code");
        private static readonly JsonEncodedText JsonPayload = JsonEncodedText.Encode("payload");
        private static readonly JsonEncodedText JsonEncoding = JsonEncodedText.Encode("encoding");
        private static readonly JsonEncodedText JsonMethod = JsonEncodedText.Encode("method");

        public const int HeaderMaxSize = 256;
        private static readonly string HeaderSizeExceededException = $"Размер заголовка сообщения превысил максимально допустимый размер в {HeaderMaxSize} байт.";

        [JsonIgnore]
        [ProtoIgnore]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string DebugDisplay
        {
            get
            {
                if (StatusCode == StatusCode.Request)
                {
                    return $"'{MethodName}', Content = {PayloadLength} байт";
                }
                else
                {
                    return $"Status = {StatusCode}, Content = {PayloadLength} байт";
                }
            }
        }

        /// <summary>
        /// true если задан <see cref="Uid"/>.
        /// </summary>
        [JsonIgnore]
        [ProtoIgnore]
        public bool IsResponseRequired => Uid != null;

        /// <summary>
        /// Это заголовок запроса когда статус равен <see cref="StatusCode.Request"/>.
        /// </summary>
        [JsonIgnore]
        [ProtoIgnore]
        public bool IsRequest => StatusCode == StatusCode.Request;

        [JsonPropertyName("code")]
        [ProtoMember(1, IsRequired = true)]
        public StatusCode StatusCode { get; }

        [JsonPropertyName("uid")]
        [ProtoMember(2, IsRequired = false)]
        public int? Uid { get; }

        [JsonPropertyName("payload")]
        [ProtoMember(3, IsRequired = false)]
        public int PayloadLength { get; }

        /// <summary>
        /// Формат контента. Может быть <see langword="null"/>, тогда 
        /// следует использовать формат по умолчанию.
        /// </summary>
        [JsonPropertyName("encoding")]
        [ProtoMember(4, IsRequired = false)]
        public string? PayloadEncoding { get; }

        /// <summary>
        /// У запроса всегда должно быть имя метода.
        /// </summary>
        [JsonPropertyName("method")]
        [ProtoMember(5, IsRequired = false)]
        public string? MethodName { get; }

        /// <summary>
        /// Конструктор запроса.
        /// </summary>
        public HeaderDto(int? uid, int payloadLength, string? contentEncoding, string actionName)
        {
            Uid = uid;
            StatusCode = StatusCode.Request;
            PayloadLength = payloadLength;
            PayloadEncoding = contentEncoding;
            MethodName = actionName;
        }

        /// <summary>
        /// Конструктор ответа на запрос.
        /// </summary>
        public HeaderDto(int uid, StatusCode responseCode, int payloadLength, string? contentEncoding)
        {
            Uid = uid;
            StatusCode = responseCode;
            PayloadLength = payloadLength;
            PayloadEncoding = contentEncoding;
            MethodName = null;
        }

        /// <summary>
        /// Конструктор и для ответа и для запроса.
        /// </summary>
        public HeaderDto(int? uid, StatusCode responseCode, int payloadLength, string? contentEncoding, string? actionName)
        {
            Uid = uid;
            StatusCode = responseCode;
            PayloadLength = payloadLength;
            PayloadEncoding = contentEncoding;
            MethodName = actionName;
        }

        /// <summary>
        /// Сериализует заголовок.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="headerSize"></param>
        public void SerializeProtoBuf(Stream stream, out int headerSize)
        {
            int initialPos = (int)stream.Position;
            
            // Сериализуем хедэр.
            ProtoBufSerializer.Serialize(stream, this);
            
            headerSize = (int)stream.Position - initialPos;

            Debug.Assert(headerSize <= HeaderMaxSize);

            if (headerSize <= HeaderMaxSize)
                return;

            ThrowHelper.ThrowVRpcException(HeaderSizeExceededException);
        }

        /// <summary>
        /// Сериализует заголовок.
        /// </summary>
        /// <remarks>Не бросает исключения.</remarks>
        public int SerializeJson(ArrayBufferWriter<byte> bufferWriter)
        {
            int initialPosition = bufferWriter.WrittenCount;

            using (var writer = new Utf8JsonWriter(bufferWriter))
            {
                writer.WriteStartObject();

                writer.WriteNumber(JsonCode, (int)StatusCode);

                if (Uid != null)
                {
                    writer.WriteNumber(JsonUid, Uid.Value);
                }
                writer.WriteNumber(JsonPayload, PayloadLength);
                if (PayloadEncoding != null)
                {
                    writer.WriteString(JsonEncoding, PayloadEncoding);
                }
                if (MethodName != null)
                {
                    writer.WriteString(JsonMethod, MethodName);
                }
                writer.WriteEndObject();
            }

            return bufferWriter.WrittenCount - initialPosition;
        }

        /// <returns>Может быть Null если не удалось десериализовать.</returns>
        public static HeaderDto DeserializeProtoBuf(byte[] buffer, int offset, int count)
        {
            HeaderDto header;
            using (var mem = new MemoryStream(buffer, offset, count))
                header = ProtoBufSerializer.Deserialize<HeaderDto>(mem);
            
            header.ValidateDeserializedHeader();
            return header;
        }

        [Conditional("DEBUG")]
        internal void ValidateDeserializedHeader()
        {
            if (IsRequest)
            {
                Debug.Assert(!string.IsNullOrEmpty(MethodName), "У запроса должно быть имя запрашиваемого метода");
            }
        }

        /// <summary>
        /// Используется только для отладки и логирования.
        /// </summary>
        public override string ToString()
        {
            string s = $"Uid = {Uid} Status = {StatusCode} Content = {PayloadLength} байт";
            if (PayloadEncoding != null)
            {
                s += $" {nameof(PayloadEncoding)} = {PayloadEncoding}";
            }
            if (MethodName != null)
            {
                s += $" '{MethodName}'";
            }
            return s;
        }

        public override int GetHashCode()
        {
            return 0;
        }

        public static bool operator ==(in HeaderDto left, in HeaderDto right)
        {
            return left.StatusCode == right.StatusCode;
        }

        public static bool operator !=(in HeaderDto left, in HeaderDto right)
        {
            return !(left == right);
        }

        public override bool Equals(object? obj)
        {
            return false;
        }

        public bool Equals(HeaderDto other)
        {
            return StatusCode == other.StatusCode;
        }
    }
}
