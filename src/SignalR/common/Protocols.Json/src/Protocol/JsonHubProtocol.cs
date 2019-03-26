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
        // TODO: private static ReadOnlySpan<byte> UrlPropertyNameBytes => new byte[] { (byte)'u', (byte)'r', (byte)'l' };
        // Use C#7.3's ReadOnlySpan<byte> optimization for static data https://vcsjones.com/2019/02/01/csharp-readonly-span-bytes-static/
        private const string ResultPropertyName = "result";
        private static ReadOnlySpan<byte> ResultPropertyNameBytes => new byte[] { (byte)'r', (byte)'e', (byte)'s', (byte)'u', (byte)'l', (byte)'t' };
        private const string ItemPropertyName = "item";
        private static ReadOnlySpan<byte> ItemPropertyNameBytes => new byte[] { (byte)'i', (byte)'t', (byte)'e', (byte)'m' };
        private const string InvocationIdPropertyName = "invocationId";
        private static ReadOnlySpan<byte> InvocationIdPropertyNameBytes => new byte[] { (byte)'i', (byte)'n', (byte)'v', (byte)'o', (byte)'c', (byte)'a', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'I', (byte)'d' };
        private const string StreamIdsPropertyName = "streamIds";
        private static ReadOnlySpan<byte> StreamIdsPropertyNameBytes => new byte[] { (byte)'s', (byte)'t', (byte)'r', (byte)'e', (byte)'a', (byte)'m', (byte)'I', (byte)'d', (byte)'s' };
        private const string TypePropertyName = "type";
        private static ReadOnlySpan<byte> TypePropertyNameBytes => new byte[] { (byte)'t', (byte)'y', (byte)'p', (byte)'e' };
        private const string ErrorPropertyName = "error";
        private static ReadOnlySpan<byte> ErrorPropertyNameBytes => new byte[] { (byte)'e', (byte)'r', (byte)'r', (byte)'o', (byte)'r' };
        private const string TargetPropertyName = "target";
        private static ReadOnlySpan<byte> TargetPropertyNameBytes => new byte[] { (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t' };
        private const string ArgumentsPropertyName = "arguments";
        private static ReadOnlySpan<byte> ArgumentsPropertyNameBytes => new byte[] { (byte)'a', (byte)'r', (byte)'g', (byte)'u', (byte)'m', (byte)'e', (byte)'n', (byte)'t', (byte)'s' };
        private const string HeadersPropertyName = "headers";
        private static ReadOnlySpan<byte> HeadersPropertyNameBytes => new byte[] { (byte)'h', (byte)'e', (byte)'a', (byte)'d', (byte)'e', (byte)'r', (byte)'s' };

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
                // We parse using the Utf8JsonReader directly but this has a problem. Some of our properties are dependent on other properties
                // and since reading the json might be unordered, we need to store the parsed content as JsonDocument to re-parse when true types are known.
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
                JsonDocument argumentsToken = null;
                JsonDocument itemsToken = null;
                JsonDocument resultToken = null;
                ExceptionDispatchInfo argumentBindingException = null;
                Dictionary<string, string> headers = null;
                var completed = false;

                var reader = new Utf8JsonReader(input, isFinalBlock: true, state: default);

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
                                    var startResultToken = reader.BytesConsumed;
                                    reader.Skip();
                                    var endResultToken = reader.BytesConsumed;

                                    // TODO: JsonDocument.Parse(ref reader);
                                    var jsonArraySequence = input.Slice(startResultToken - 1, endResultToken - startResultToken + 1);
                                    resultToken = JsonDocument.Parse(jsonArraySequence);
                                }
                                else
                                {
                                    // If we have an invocation id already we can parse the end result
                                    var returnType = binder.GetReturnType(invocationId);

                                    var startResultToken = reader.BytesConsumed;
                                    reader.Skip();
                                    var endResultToken = reader.BytesConsumed;

                                    // TODO: JsonDocument.Parse(ref reader);
                                    var jsonArraySequence = input.Slice(startResultToken - 1, endResultToken - startResultToken + 1);
                                    using var token = JsonDocument.Parse(jsonArraySequence);
                                    result = BindType(token.RootElement, returnType);
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
                                    var startItemsToken = reader.BytesConsumed;
                                    reader.Skip();
                                    var endItemsToken = reader.BytesConsumed;

                                    // TODO: JsonDocument.Parse(ref reader);
                                    var jsonArraySequence = input.Slice(startItemsToken - 1, endItemsToken - startItemsToken + 1);
                                    itemsToken = JsonDocument.Parse(jsonArraySequence);
                                    continue;
                                }

                                try
                                {
                                    var itemType = binder.GetStreamItemType(id);

                                    var startItemsToken = reader.BytesConsumed;
                                    reader.Skip();
                                    var endItemsToken = reader.BytesConsumed;

                                    // TODO: JsonDocument.Parse(ref reader);
                                    var jsonArraySequence = input.Slice(startItemsToken - 1, endItemsToken - startItemsToken + 1);
                                    var token = JsonDocument.Parse(jsonArraySequence);
                                    result = BindType(token.RootElement, itemType);
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
                                    var startArgumentsToken = reader.BytesConsumed;
                                    reader.Skip();
                                    var endArgumentsToken = reader.BytesConsumed;

                                    // TODO: JsonDocument.Parse(ref reader);
                                    var jsonArraySequence = input.Slice(startArgumentsToken - 1, endArgumentsToken - startArgumentsToken + 1);
                                    argumentsToken = JsonDocument.Parse(jsonArraySequence);
                                }
                                else
                                {
                                    try
                                    {
                                        var paramTypes = binder.GetParameterTypes(target);

                                        // TODO: JsonDocument.Parse(ref reader);
                                        var startItemsToken = reader.BytesConsumed;
                                        reader.Skip();
                                        var endItemsToken = reader.BytesConsumed;
                                        var jsonArraySequence = input.Slice(startItemsToken - 1, endItemsToken - startItemsToken + 1);

                                        using var token = JsonDocument.Parse(jsonArraySequence);
                                        arguments = BindTypes(token.RootElement, paramTypes);
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
                                headers = ReadHeaders(ref reader);
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

                HubMessage message;

                switch (type)
                {
                    case HubProtocolConstants.InvocationMessageType:
                        {
                            if (argumentsToken != null)
                            {
                                // We weren't able to bind the arguments because they came before the 'target', so try to bind now that we've read everything.
                                try
                                {
                                    var paramTypes = binder.GetParameterTypes(target);
                                    arguments = BindTypes(argumentsToken.RootElement, paramTypes);
                                }
                                catch (Exception ex)
                                {
                                    argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                                }
                                finally
                                {
                                    argumentsToken.Dispose();
                                }
                            }

                            message = argumentBindingException != null
                                ? new InvocationBindingFailureMessage(invocationId, target, argumentBindingException)
                                : BindInvocationMessage(invocationId, target, arguments, hasArguments, streamIds, binder);
                        }
                        break;
                    case HubProtocolConstants.StreamInvocationMessageType:
                        {
                            if (argumentsToken != null)
                            {
                                // We weren't able to bind the arguments because they came before the 'target', so try to bind now that we've read everything.
                                try
                                {
                                    var paramTypes = binder.GetParameterTypes(target);
                                    arguments = BindTypes(argumentsToken.RootElement, paramTypes);
                                }
                                catch (Exception ex)
                                {
                                    argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                                }
                                finally
                                {
                                    argumentsToken.Dispose();
                                }
                            }

                            message = argumentBindingException != null
                                ? new InvocationBindingFailureMessage(invocationId, target, argumentBindingException)
                                : BindStreamInvocationMessage(invocationId, target, arguments, hasArguments, streamIds, binder);
                        }
                        break;
                    case HubProtocolConstants.StreamItemMessageType:
                        if (itemsToken != null)
                        {
                            try
                            {
                                var returnType = binder.GetReturnType(invocationId);
                                item = BindType(itemsToken.RootElement, returnType);
                            }
                            catch (JsonReaderException ex)
                            {
                                message = new StreamBindingFailureMessage(invocationId, ExceptionDispatchInfo.Capture(ex));
                                break;
                            }
                            finally
                            {
                                itemsToken.Dispose();
                            }
                        }

                        message = BindStreamItemMessage(invocationId, item, hasItem, binder);
                        break;
                    case HubProtocolConstants.CompletionMessageType:
                        if (resultToken != null)
                        {
                            try
                            {
                                var returnType = binder.GetReturnType(invocationId);
                                result = BindType(resultToken.RootElement, returnType);
                            }
                            finally
                            {
                                resultToken.Dispose();
                            }
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

        private Dictionary<string, string> ReadHeaders(ref Utf8JsonReader reader)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException($"Expected '{HeadersPropertyName}' to be of type {JsonTokenType.StartObject}.");
            }

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        var propertyName = reader.GetString();

                        reader.CheckRead();

                        if (reader.TokenType != JsonTokenType.String)
                        {
                            throw new InvalidDataException($"Expected header '{propertyName}' to be of type {JsonTokenType.String}.");
                        }

                        headers[propertyName] = reader.GetString();
                        break;
                    case JsonTokenType.Comment:
                        break;
                    case JsonTokenType.EndObject:
                        return headers;
                }
            }

            throw new JsonReaderException("Unexpected end when reading message headers", 0, 0);
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
                // TODO: JsonElement.WriteTo(ref writer, ResultPropertyNameBytes, escape: false);
                var bytes = JsonSerializer.ToBytes(message.Result, message.Result.GetType());
                // TODO: JsonElement.WriteValueTo(ref writer, bytes, escape: false);
            }
        }

        private void WriteCancelInvocationMessage(CancelInvocationMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
        }

        private void WriteStreamItemMessage(StreamItemMessage message, ref Utf8JsonWriter writer)
        {
            WriteInvocationId(message, ref writer);
            // TODO: JsonElement.WriteTo(ref writer, ItemPropertyNameBytes, escape: false);
            var bytes = JsonSerializer.ToBytes(message.Item, message.Item.GetType());
            // TODO: JsonElement.WriteValueTo(ref writer, bytes, escape: false);
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

        private object BindType(JsonElement jsonObject, Type type)
        {
            if (type == typeof(DateTime))
            {
                // TODO: return jsonObject.GetDateTime();
            }
            else if (type == typeof(DateTimeOffset))
            {
                // TODO: return jsonObject.GetDateTimeOffset();
            }

            return JsonSerializer.Parse(jsonObject.GetRawText(), type);
        }

        private object[] BindTypes(JsonElement jsonArray, IReadOnlyList<Type> paramTypes)
        {
            object[] arguments = null;
            var paramIndex = 0;
            var argumentsCount = jsonArray.GetArrayLength();
            var paramCount = paramTypes.Count;

            if (argumentsCount != paramCount)
            {
                throw new InvalidDataException($"Invocation provides {argumentsCount} argument(s) but target expects {paramCount}.");
            }

            foreach (var element in jsonArray.EnumerateArray())
            {
                if (arguments == null)
                {
                    arguments = new object[paramCount];
                }

                try
                {
                    arguments[paramIndex] = BindType(element, paramTypes[paramIndex]);
                    ++paramIndex;
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
                }
            }

            return arguments ?? Array.Empty<object>();
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
