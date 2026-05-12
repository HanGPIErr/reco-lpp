using System;
using System.Net.Http;
using FluentAssertions;
using RecoTool.Utils;
using Xunit;

namespace RecoTool.Tests.Utils
{
    /// <summary>
    /// Tests pour <see cref="RequestCache"/> — cache TTL pour HttpRequestMessage.
    /// </summary>
    public class RequestCacheTests
    {
        // ----- GenerateKey -----

        [Fact]
        public void GenerateKey_GetWithoutBody_BuildsMethodPlusPath()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost/api/foo"));
            var key = RequestCache.GenerateKey(req);
            // Pour URL absolue, c'est l'AbsolutePath qui est utilisé
            key.Should().Be("GET:/api/foo:");
        }

        [Fact]
        public void GenerateKey_RelativeUriUsesFullToString()
        {
            // Relative Uri — pas absolue → on garde RequestUri.ToString()
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/bar", UriKind.Relative))
            {
                Content = new StringContent("payload")
            };
            var key = RequestCache.GenerateKey(req);
            key.Should().StartWith("POST:/api/bar:");
            key.Should().EndWith("payload");
        }

        [Fact]
        public void GenerateKey_DifferentBodies_ProduceDifferentKeys()
        {
            var r1 = new HttpRequestMessage(HttpMethod.Post, new Uri("http://x/y"))
            {
                Content = new StringContent("a")
            };
            var r2 = new HttpRequestMessage(HttpMethod.Post, new Uri("http://x/y"))
            {
                Content = new StringContent("b")
            };
            RequestCache.GenerateKey(r1).Should().NotBe(RequestCache.GenerateKey(r2));
        }

        // ----- TryGet / Set -----

        [Fact]
        public void TryGet_BeforeSet_ReturnsFalseAndNullJson()
        {
            var c = new RequestCache();
            c.TryGet("X", out var json).Should().BeFalse();
            json.Should().BeNull();
        }

        [Fact]
        public void Set_ThenTryGet_ReturnsValue()
        {
            var c = new RequestCache();
            c.Set("K", "{\"foo\":\"bar\"}");
            c.TryGet("K", out var json).Should().BeTrue();
            json.Should().Be("{\"foo\":\"bar\"}");
        }

        [Fact]
        public void Set_OverwritesPrevious()
        {
            var c = new RequestCache();
            c.Set("K", "v1");
            c.Set("K", "v2");
            c.TryGet("K", out var json).Should().BeTrue();
            json.Should().Be("v2");
        }
    }
}
