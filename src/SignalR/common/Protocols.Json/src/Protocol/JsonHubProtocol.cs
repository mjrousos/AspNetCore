// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.SignalR.Protocol
{
    /// <summary>
    /// Implements the SignalR Hub Protocol using System.Text.Json.
    /// </summary>
    public class JsonHubProtocol : IHubProtocol
    {
        private const string ResultPropertyName = "result";
        private static readonly byte[] ResultPropertyNameBytes = Encoding.UTF8.GetBytes(ResultPropertyName);
        private const string ItemPropertyName = "item";
        private static readonly byte[] ItemPropertyNameBytes = Encoding.UTF8.GetBytes(ItemPropertyName);
        private const string InvocationIdPropertyName = "invocationId";
        private static readonly byte[] InvocationIdPropertyNameBytes = Encoding.UTF8.GetBytes(InvocationIdPropertyName);
        private const string StreamIdsPropertyName = "streamIds";
        private static readonly byte[] StreamIdsPropertyNameBytes = Encoding.UTF8.GetBytes(StreamIdsPropertyName);
        private const string TypePropertyName = "type";
        private static readonly byte[] TypePropertyNameBytes = Encoding.UTF8.GetBytes(TypePropertyName);
        private const string ErrorPropertyName = "error";
        private static readonly byte[] ErrorPropertyNameBytes = Encoding.UTF8.GetBytes(ErrorPropertyName);
        private const string TargetPropertyName = "target";
        private static readonly byte[] TargetPropertyNameBytes = Encoding.UTF8.GetBytes(TargetPropertyName);
        private const string ArgumentsPropertyName = "arguments";
        private static readonly byte[] ArgumentsPropertyNameBytes = Encoding.UTF8.GetBytes(ArgumentsPropertyName);
        private const string HeadersPropertyName = "headers";
        private static readonly byte[] HeadersPropertyNameBytes = Encoding.UTF8.GetBytes(HeadersPropertyName);

        private static readonly string ProtocolName = "json";
        private static readonly int ProtocolVersion = 1;
        private static readonly int ProtocolMinorVersion = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonHubProtocol"/> class.
        /// </summary>
        public JsonHubProtocol() : this(Options.Create(new JsonHubProtocolOptions()))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonHubProtocol"/> class.
        /// </summary>
        /// <param name="options">The options used to initialize the protocol.</param>
        public JsonHubProtocol(IOptions<JsonHubProtocolOptions> options)
        {
        }

        /// <inheritdoc />
        public string Name => ProtocolName;

        /// <inheritdoc />
        public int Version => ProtocolVersion;

        /// <inheritdoc />        
        public int MinorVersion => ProtocolMinorVersion;

        /// <inheritdoc />
        public TransferFormat TransferFormat => TransferFormat.Text;

        /// <inheritdoc />
        public bool IsVersionSupported(int version)
        {
            return version == Version;
        }

        /// <inheritdoc />
        public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, out HubMessage message)
        {
            if (!TextMessageParser.TryParseMessage(ref input, out var payload))
            {
                message = null;
                return false;
            }

            message = ParseMessage(payload, binder);

            return message != null;
        }

        /// <inheritdoc />
        public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
        {
            WriteMessageCore(message, output);
            TextMessageFormatter.WriteRecordSeparator(output);
        }

        /// <inheritdoc />
        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
        {
            return HubProtocolExtensions.GetMessageBytes(this, message);
        }

        private HubMessage ParseMessage(ReadOnlySequence<byte> input, IInvocationBinder binder)
        {
            try
            {
                // We parse using the JsonTextReader directly but this has a problem. Some of our properties are dependent on other properties
                // and since reading the json might be unordered, we need to store the parsed content as JToken to re-parse when true types are known.
                // if we're lucky and the state we need to directly parse is available, then we'll use it.

                int? type = null;
                string invocationId = null;
                string target = null;
                string error = null;
                var hasItem = false;
                object item = null;
                var hasResult = false;
                object result = null;
                var hasArguments = false;
                object[] arguments = null;
                string[] streamIds = null;
                long startArgumentsToken = 0;
                long endArgumentsToken = 0;
                long startItemsToken = 0;
                long endItemsToken = 0;
                long startResultToken = 0;
                long endResultToken = 0;
                ExceptionDispatchInfo argumentBindingException = null;
                Dictionary<string, string> headers = null;
                var completed = false;

                //var b = new byte[] { 123, 34, 116, 121, 112, 101, 34, 58, 49, 44, 34, 105, 110, 118, 111, 99, 97, 116, 105, 111, 110, 73, 100, 34, 58, 34, 49, 50, 51, 34, 44, 34, 116, 97, 114, 103, 101, 116, 34, 58, 34, 84, 97, 114, 103, 101, 116, 34, 44, 34, 97, 114, 103, 117, 109, 101, 110, 116, 115, 34, 58, 91, 49, 44, 34, 70, 111, 111, 34, 44, 50, 46, 48, 93, 125, 30 };

                var reader = new Utf8JsonReader(input, isFinalBlock: true, state: default);

                //using (var reader = JsonUtils.CreateJsonTextReader(textReader))
                {
                    //reader.DateParseHandling = DateParseHandling.None;

                    reader.CheckRead();

                    // We're always parsing a JSON object
                    reader.EnsureObjectStart();

                    do
                    {
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.PropertyName:
                                var memberName = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;

                                if (memberName.SequenceEqual(TypePropertyNameBytes))
                                {
                                    type = reader.ReadAsInt32(TypePropertyName);

                                    if (type == null)
                                    {
                                        throw new InvalidDataException($"Missing required property '{TypePropertyName}'.");
                                    }
                                }
                                else if (memberName.SequenceEqual(InvocationIdPropertyNameBytes))
                                {
                                    invocationId = reader.ReadAsString(InvocationIdPropertyName);
                                }
                                else if (memberName.SequenceEqual(StreamIdsPropertyNameBytes))
                                {
                                    reader.CheckRead();

                                    if (reader.TokenType != JsonTokenType.StartArray)
                                    {
                                        throw new InvalidDataException($"Expected '{StreamIdsPropertyName}' to be of type {reader.GetTokenString()}.");
                                    }

                                    var newStreamIds = new List<string>();
                                    reader.Read();
                                    while (reader.TokenType != JsonTokenType.EndArray)
                                    {
                                        newStreamIds.Add(reader.GetString());
                                        reader.Read();
                                    }

                                    streamIds = newStreamIds.ToArray();
                                }
                                else if (memberName.SequenceEqual(TargetPropertyNameBytes))
                                {
                                    target = reader.ReadAsString(TargetPropertyName);
                                }
                                else if (memberName.SequenceEqual(ErrorPropertyNameBytes))
                                {
                                    error = reader.ReadAsString(ErrorPropertyName);
                                }
                                else if (memberName.SequenceEqual(ResultPropertyNameBytes))
                                {
                                    hasResult = true;

                                    if (string.IsNullOrEmpty(invocationId))
                                    {
                                        reader.CheckRead();

                                        // If we don't have an invocation id then we need to store it as a JToken so we can parse it later
                                        startResultToken = reader.BytesConsumed;
                                        int depth = reader.CurrentDepth;
                                        while (reader.Read() && depth <= reader.CurrentDepth)
                                        {
                                        }
                                        endResultToken = reader.BytesConsumed;
                                    }
                                    else
                                    {
                                        // If we have an invocation id already we can parse the end result
                                        var returnType = binder.GetReturnType(invocationId);

                                        reader.Read();

                                        var bytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                                        result = JsonSerializer.Parse(bytes, returnType);
                                    }
                                }
                                else if (memberName.SequenceEqual(ItemPropertyNameBytes))
                                {
                                    reader.CheckRead();

                                    hasItem = true;

                                    string id = null;
                                    if (!string.IsNullOrEmpty(invocationId))
                                    {
                                        id = invocationId;
                                    }
                                    else
                                    {
                                        // If we don't have an id yet then we need to store it as a JToken to parse later
                                        startItemsToken = reader.BytesConsumed;
                                        int depth = reader.CurrentDepth;
                                        while (reader.Read() && depth <= reader.CurrentDepth)
                                        {
                                        }
                                        endItemsToken = reader.BytesConsumed;
                                        continue;
                                    }

                                    try
                                    {
                                        var itemType = binder.GetStreamItemType(id);

                                        var bytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                                        item = JsonSerializer.Parse(bytes, itemType);
                                    }
                                    catch (Exception ex)
                                    {
                                        return new StreamBindingFailureMessage(id, ExceptionDispatchInfo.Capture(ex));
                                    }
                                }
                                else if (memberName.SequenceEqual(ArgumentsPropertyNameBytes))
                                {
                                    reader.CheckRead();

                                    int initialDepth = reader.CurrentDepth;
                                    if (reader.TokenType != JsonTokenType.StartArray)
                                    {
                                        throw new InvalidDataException($"Expected '{ArgumentsPropertyName}' to be of type {reader.GetTokenString()}.");
                                    }

                                    hasArguments = true;

                                    if (string.IsNullOrEmpty(target))
                                    {
                                        // We don't know the method name yet so just parse an array of generic JArray
                                        startArgumentsToken = reader.BytesConsumed;
                                        int depth = reader.CurrentDepth;
                                        while (reader.Read() && depth <= reader.CurrentDepth)
                                        {
                                        }
                                        endArgumentsToken = reader.BytesConsumed;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            var paramTypes = binder.GetParameterTypes(target);
                                            arguments = BindArguments(ref reader, paramTypes);
                                        }
                                        catch (Exception ex)
                                        {
                                            argumentBindingException = ExceptionDispatchInfo.Capture(ex);

                                            // Could be at any point in argument array JSON when an error is thrown
                                            // Read until the end of the argument JSON array
                                            while (reader.CurrentDepth == initialDepth && reader.TokenType == JsonTokenType.StartArray ||
                                                   reader.CurrentDepth > initialDepth)
                                            {
                                                reader.CheckRead();
                                            }
                                        }
                                    }
                                }
                                else if (memberName.SequenceEqual(HeadersPropertyNameBytes))
                                {
                                    reader.CheckRead();
                                    //headers = ReadHeaders(reader);
                                }
                                else
                                {
                                    reader.CheckRead();
                                    reader.Skip();
                                }
                                break;
                            case JsonTokenType.EndObject:
                                completed = true;
                                break;
                        }
                    }
                    while (!completed && reader.CheckRead());
                }

                HubMessage message;

                switch (type)
                {
                    case HubProtocolConstants.InvocationMessageType:
                        {
                            if (startArgumentsToken != 0)
                            {
                                // We weren't able to bind the arguments because they came before the 'target', so try to bind now that we've read everything.
                                try
                                {
                                    var paramTypes = binder.GetParameterTypes(target);
                                    var jsonArraySequence = input.Slice(startArgumentsToken - 1, endArgumentsToken - startArgumentsToken + 1);
                                    arguments = BindArguments(jsonArraySequence, paramTypes);
                                }
                                catch (Exception ex)
                                {
                                    argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                                }
                            }

                            message = argumentBindingException != null
                                ? new InvocationBindingFailureMessage(invocationId, target, argumentBindingException)
                                : BindInvocationMessage(invocationId, target, arguments, hasArguments, streamIds, binder);
                        }
                        break;
                    case HubProtocolConstants.StreamInvocationMessageType:
                        {
                            if (startArgumentsToken != 0)
                            {
                                // We weren't able to bind the arguments because they came before the 'target', so try to bind now that we've read everything.
                                try
                                {
                                    var paramTypes = binder.GetParameterTypes(target);
                                    var jsonArraySequence = input.Slice(startArgumentsToken - 1, endArgumentsToken - startArgumentsToken + 1);
                                    arguments = BindArguments(jsonArraySequence, paramTypes);
                                }
                                catch (Exception ex)
                                {
                                    argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                                }
                            }

                            message = argumentBindingException != null
                                ? new InvocationBindingFailureMessage(invocationId, target, argumentBindingException)
                                : BindStreamInvocationMessage(invocationId, target, arguments, hasArguments, streamIds, binder);
                        }
                        break;
                    case HubProtocolConstants.StreamItemMessageType:
                        if (startItemsToken != 0)
                        {
                            try
                            {
                                var returnType = binder.GetReturnType(invocationId);
                                var jsonArraySequence = input.Slice(startItemsToken - 1, endItemsToken - startItemsToken + 1);
                                item = BindArgument(jsonArraySequence, returnType);
                            }
                            catch (JsonReaderException ex)
                            {
                                message = new StreamBindingFailureMessage(invocationId, ExceptionDispatchInfo.Capture(ex));
                                break;
                            };
                        }

                        message = BindStreamItemMessage(invocationId, item, hasItem, binder);
                        break;
                    case HubProtocolConstants.CompletionMessageType:
                        if (startResultToken != 0)
                        {
                            var returnType = binder.GetReturnType(invocationId);
                            var jsonArraySequence = input.Slice(startResultToken - 1, endResultToken - startResultToken + 1);
                            result = BindArgument(jsonArraySequence, returnType);
                        }

                        message = BindCompletionMessage(invocationId, error, result, hasResult, binder);
                        break;
                    case HubProtocolConstants.CancelInvocationMessageType:
                        message = BindCancelInvocationMessage(invocationId);
                        break;
                    case HubProtocolConstants.PingMessageType:
                        return PingMessage.Instance;
                    case HubProtocolConstants.CloseMessageType:
                        return BindCloseMessage(error);
                    case null:
                        throw new InvalidDataException($"Missing required property '{TypePropertyName}'.");
                    default:
                        // Future protocol changes can add message types, old clients can ignore them
                        return null;
                }

                return ApplyHeaders(message, headers);
            }
            catch (JsonReaderException jrex)
            {
                throw new InvalidDataException("Error reading JSON.", jrex);
            }
        }

        private void WriteMessageCore(HubMessage message, IBufferWriter<byte> stream)
        {
            var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            switch (message)
            {
                case InvocationMessage m:
                    WriteMessageType(ref writer, HubProtocolConstants.InvocationMessageType);
                    WriteHeaders(ref writer, m);
                    WriteInvocationMessage(stream, m, ref writer);
                    break;
                case StreamInvocationMessage m:
                    WriteMessageType(ref writer, HubProtocolConstants.StreamInvocationMessageType);
                    WriteHeaders(ref writer, m);
                    WriteStreamInvocationMessage(m, ref writer);
                    break;
                case StreamItemMessage m:
                    WriteMessageType(ref writer, HubProtocolConstants.StreamItemMessageType);
                    WriteHeaders(ref writer, m);
                    WriteStreamItemMessage(m, ref writer);
                    break;
                case CompletionMessage m:
                    WriteMessageType(ref writer, HubProtocolConstants.CompletionMessageType);
                    WriteHeaders(ref writer, m);
                    WriteCompletionMessage(m, ref writer);
                    break;
                case CancelInvocationMessage m:
                    WriteMessageType(ref writer, HubProtocolConstants.CancelInvocationMessageType);
                    WriteHeaders(ref writer, m);
                    WriteCancelInvocationMessage(m, ref writer);
                    break;
                case PingMessage _:
                    WriteMessageType(ref writer, HubProtocolConstants.PingMessageType);
                    break;
                case CloseMessage m:
                    WriteMessageType(ref writer, HubProtocolConstants.CloseMessageType);
                    WriteCloseMessage(m, ref writer);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported message type: {message.GetType().FullName}");
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private void WriteHeaders(ref Utf8JsonWriter writer, HubInvocationMessage message)
        {
            if (message.Headers != null && message.Headers.Count > 0)
            {
                writer.WriteStartObject(HeadersPropertyNameBytes);
                foreach (var value in message.Headers)
                {
                    writer.WriteString(value.Key, value.Value);
                }
                writer.WriteEndObject();
            }
        }

        private void WriteCompletionMessage(CompletionMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
            if (!string.IsNullOrEmpty(message.Error))
            {
                writer.WriteString(ErrorPropertyNameBytes, message.Error);
            }
            else if (message.HasResult)
            {
                //writer.WritePropertyName(ResultPropertyName);
                //PayloadSerializer.Serialize(writer, message.Result);
            }
        }

        private void WriteCancelInvocationMessage(CancelInvocationMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
        }

        private void WriteStreamItemMessage(StreamItemMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
            //writer.WritePropertyName(ItemPropertyName);
            //PayloadSerializer.Serialize(writer, message.Item);
        }

        private void WriteInvocationMessage(IBufferWriter<byte> stream, InvocationMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
            writer.WriteString(TargetPropertyNameBytes, message.Target);

            WriteArguments(stream, message.Arguments, ref writer);

            WriteStreamIds(message.StreamIds, ref writer);
        }

        private void WriteStreamInvocationMessage(StreamInvocationMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
            writer.WriteString(TargetPropertyNameBytes, message.Target);

            //WriteArguments(message.Arguments, ref writer);

            WriteStreamIds(message.StreamIds, ref writer);
        }

        private void WriteCloseMessage(CloseMessage message, ref Utf8JsonWriter writer)
        {
            if (message.Error != null)
            {
                writer.WriteString(ErrorPropertyNameBytes, message.Error);
            }
        }

        private void WriteArguments(IBufferWriter<byte> stream, object[] arguments, ref Utf8JsonWriter writer)
        {
            writer.WriteStartArray(ArgumentsPropertyNameBytes);
            writer.Flush(isFinalBlock: false);
            foreach (var argument in arguments)
            {
                var bytes = JsonSerializer.ToBytes(argument, argument.GetType());
                WriteRaw(stream, bytes, ref writer);
            }
            writer.WriteEndArray();
        }

        private void WriteRaw(IBufferWriter<byte> stream, byte[] bytes, ref Utf8JsonWriter writer)
        {
            // Super hack (only works for small payloads)
            // First write a string to the writer (slicing 2 for the quotes)
            // to advance it's internal buffer
            writer.WriteStringValue(bytes.AsSpan().Slice(0, bytes.Length - 2), escape: false);
            // Then write the real bytes to the real buffer
            stream.Write(bytes);
            // Don't advance the stream when writing, so the writer advances correctly later
            stream.Advance(-bytes.Length);
        }

        private void WriteStreamIds(string[] streamIds, ref Utf8JsonWriter writer)
        {
            if (streamIds == null)
            {
                return;
            }

            writer.WriteStartArray(StreamIdsPropertyNameBytes);
            writer.WriteStartArray();
            foreach (var streamId in streamIds)
            {
                writer.WriteStringValue(streamId);
            }
            writer.WriteEndArray();
        }

        private static void WriteInvocationId(HubInvocationMessage message, ref Utf8JsonWriter writer)
        {
            if (!string.IsNullOrEmpty(message.InvocationId))
            {
                writer.WriteString(InvocationIdPropertyNameBytes, message.InvocationId);
            }
        }

        private static void WriteMessageType(ref Utf8JsonWriter writer, int type)
        {
            writer.WriteNumber(TypePropertyNameBytes, type);
        }

        private HubMessage BindCancelInvocationMessage(string invocationId)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            return new CancelInvocationMessage(invocationId);
        }

        private HubMessage BindCompletionMessage(string invocationId, string error, object result, bool hasResult, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            if (error != null && hasResult)
            {
                throw new InvalidDataException("The 'error' and 'result' properties are mutually exclusive.");
            }

            if (hasResult)
            {
                return new CompletionMessage(invocationId, error, result, hasResult: true);
            }

            return new CompletionMessage(invocationId, error, result: null, hasResult: false);
        }

        private HubMessage BindStreamItemMessage(string invocationId, object item, bool hasItem, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            if (!hasItem)
            {
                throw new InvalidDataException($"Missing required property '{ItemPropertyName}'.");
            }

            return new StreamItemMessage(invocationId, item);
        }

        private HubMessage BindStreamInvocationMessage(string invocationId, string target, object[] arguments, bool hasArguments, string[] streamIds, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(invocationId))
            {
                throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
            }

            if (!hasArguments)
            {
                throw new InvalidDataException($"Missing required property '{ArgumentsPropertyName}'.");
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidDataException($"Missing required property '{TargetPropertyName}'.");
            }

            return new StreamInvocationMessage(invocationId, target, arguments, streamIds);
        }

        private HubMessage BindInvocationMessage(string invocationId, string target, object[] arguments, bool hasArguments, string[] streamIds, IInvocationBinder binder)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidDataException($"Missing required property '{TargetPropertyName}'.");
            }

            if (!hasArguments)
            {
                throw new InvalidDataException($"Missing required property '{ArgumentsPropertyName}'.");
            }

            return new InvocationMessage(invocationId, target, arguments, streamIds);
        }

        private bool ReadArgumentAsType(ref Utf8JsonReader reader, IReadOnlyList<Type> paramTypes, int paramIndex)
        {
            if (paramIndex < paramTypes.Count)
            {
                var paramType = paramTypes[paramIndex];

                // ReadForType
                return reader.Read();
            }

            return reader.Read();
        }

        private object BindArgument(ReadOnlySequence<byte> arrayBytes, Type type)
        {
            var reader = new Utf8JsonReader(arrayBytes, false, default);
            reader.Read();
            return BindArgument(ref reader, type);
        }

        private object BindArgument(ref Utf8JsonReader reader, Type type)
        {
            try
            {
                var bytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                if (type == typeof(DateTime))
                {
                    return DateTime.Parse(Encoding.UTF8.GetString(bytes));
                }
                else
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var b = new byte[bytes.Length + 2];
                        b[0] = 34;
                        bytes.CopyTo(b.AsSpan().Slice(1, bytes.Length));
                        b[bytes.Length + 1] = 34;
                        bytes = b.AsSpan();
                    }
                    return JsonSerializer.Parse(bytes, type);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
            }
        }

        private object[] BindArguments(ReadOnlySequence<byte> arrayBytes, IReadOnlyList<Type> paramTypes)
        {
            var reader = new Utf8JsonReader(arrayBytes, false, default);
            reader.Read();
            return BindArguments(ref reader, paramTypes);
        }

        private object[] BindArguments(ref Utf8JsonReader reader, IReadOnlyList<Type> paramTypes)
        {
            object[] arguments = null;
            var paramIndex = 0;
            var argumentsCount = 0;
            var paramCount = paramTypes.Count;

            while (ReadArgumentAsType(ref reader, paramTypes, paramIndex))
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (argumentsCount != paramCount)
                    {
                        throw new InvalidDataException($"Invocation provides {argumentsCount} argument(s) but target expects {paramCount}.");
                    }

                    return arguments ?? Array.Empty<object>();
                }

                if (arguments == null)
                {
                    arguments = new object[paramCount];
                }

                try
                {
                    if (paramIndex < paramCount)
                    {
                        var bytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                        if (paramTypes[paramIndex] == typeof(DateTime))
                        {
                            arguments[paramIndex] = DateTime.Parse(Encoding.UTF8.GetString(bytes));
                        }
                        else
                        {
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                var b = new byte[bytes.Length + 2];
                                b[0] = 34;
                                bytes.CopyTo(b.AsSpan().Slice(1, bytes.Length));
                                b[bytes.Length + 1] = 34;
                                bytes = b.AsSpan();
                            }
                            arguments[paramIndex] = JsonSerializer.Parse(bytes, paramTypes[paramIndex]);
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }

                    argumentsCount++;
                    paramIndex++;
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
                }
            }

            throw new JsonReaderException("Unexpected end when reading JSON", 0, 0);
        }

        private CloseMessage BindCloseMessage(string error)
        {
            // An empty string is still an error
            if (error == null)
            {
                return CloseMessage.Empty;
            }

            var message = new CloseMessage(error);
            return message;
        }

        private HubMessage ApplyHeaders(HubMessage message, Dictionary<string, string> headers)
        {
            if (headers != null && message is HubInvocationMessage invocationMessage)
            {
                invocationMessage.Headers = headers;
            }

            return message;
        }
    }
}
