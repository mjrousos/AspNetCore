// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Protocol;
using Xunit;

namespace Microsoft.AspNetCore.SignalR.Common.Tests.Internal.Protocol
{
    public class JsonHubProtocolTests : JsonHubProtocolTestsBase
    {
        protected override IHubProtocol JsonHubProtocol => new JsonHubProtocol();

        [Fact]
        public void TempTest()
        {
            var bytes = Encoding.UTF8.GetBytes("13");
            var obj = JsonSerializer.Parse(bytes, typeof(object));
            Assert.Equal(13, (int)obj);
        }
    }
}
