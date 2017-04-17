// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Mvc.TestCommon;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Microsoft.AspNetCore.Mvc
{
    public class FileContentResultTest
    {
        [Fact]
        public void Constructor_SetsFileContents()
        {
            // Arrange
            var fileContents = new byte[0];

            // Act
            var result = new FileContentResult(fileContents, "text/plain");

            // Assert
            Assert.Same(fileContents, result.FileContents);
        }

        [Fact]
        public void Constructor_SetsContentTypeAndParameters()
        {
            // Arrange
            var fileContents = new byte[0];
            var contentType = "text/plain; charset=us-ascii; p1=p1-value";
            var expectedMediaType = contentType;

            // Act
            var result = new FileContentResult(fileContents, contentType);

            // Assert
            Assert.Same(fileContents, result.FileContents);
            MediaTypeAssert.Equal(expectedMediaType, result.ContentType);
        }

        [Fact]
        public void Constructor_SetsRangeProcessingParameters()
        {
            // Arrange
            var fileContents = new byte[0];
            var contentType = "text/plain";
            var expectedMediaType = contentType;
            var lastModified = new DateTimeOffset();
            var entityTag = new EntityTagHeaderValue("\"Etag\"");

            // Act
            var result = new FileContentResult(
                fileContents: fileContents,
                contentType: new MediaTypeHeaderValue(contentType),
                enableRangeProcessing: true,
                lastModified: lastModified,
                entityTag: entityTag);

            // Assert
            Assert.True(result.EnableRangeProcessing);
            Assert.Equal(lastModified, result.LastModified);
            Assert.Equal(entityTag, result.EntityTag);
            MediaTypeAssert.Equal(expectedMediaType, result.ContentType);
        }

        [Fact]
        public async Task WriteFileAsync_CopiesBuffer_ToOutputStream()
        {
            // Arrange
            var buffer = new byte[] { 1, 2, 3, 4, 5 };

            var httpContext = GetHttpContext();

            var outStream = new MemoryStream();
            httpContext.Response.Body = outStream;

            var context = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            var result = new FileContentResult(buffer, "text/plain");

            // Act
            await result.ExecuteResultAsync(context);

            // Assert
            Assert.Equal(buffer, outStream.ToArray());
        }

        [Theory]
        [InlineData(0, 4, "Hello", 5)]
        [InlineData(6, 10, "World", 5)]
        public async Task WriteFileAsync_WritesRangeRequested(long start, long end, string expectedString, long contentLength)
        {
            // Arrange            
            var contentType = "text/plain";
            var expectedMediaType = contentType;
            var lastModified = new DateTimeOffset();
            var entityTag = new EntityTagHeaderValue("\"Etag\"");
            var byteArray = Encoding.ASCII.GetBytes("Hello World");

            var result = new FileContentResult(
                fileContents: byteArray,
                contentType: new MediaTypeHeaderValue(contentType),
                enableRangeProcessing: true,
                lastModified: lastModified,
                entityTag: entityTag);

            var httpContext = GetHttpContext();
            httpContext.Request.GetTypedHeaders().Range = new RangeHeaderValue(start, end);
            httpContext.Request.Method = "GET";
            httpContext.Response.Body = new MemoryStream();

            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            // Act
            await result.ExecuteResultAsync(actionContext);
            var httpResponse = actionContext.HttpContext.Response;
            httpResponse.Body.Seek(0, SeekOrigin.Begin);
            var streamReader = new StreamReader(httpResponse.Body);
            var body = streamReader.ReadToEndAsync().Result;
            var contentRange = new ContentRangeHeaderValue(start, end, byteArray.Length);

            // Assert
            Assert.Equal(StatusCodes.Status206PartialContent, httpResponse.StatusCode);
            Assert.Equal("bytes", httpResponse.Headers[HeaderNames.AcceptRanges]);
            Assert.Equal(contentRange.ToString(), httpResponse.Headers[HeaderNames.ContentRange]);
            Assert.Equal(lastModified.ToString("R"), httpResponse.Headers[HeaderNames.LastModified]);
            Assert.Equal(entityTag.ToString(), httpResponse.Headers[HeaderNames.ETag]);
            Assert.Equal(contentLength, httpResponse.ContentLength);
            Assert.Equal(expectedString, body);
        }

        [Theory]
        [InlineData("11-0")]
        [InlineData("1-4, 5-11")]
        public async Task WriteFileAsync_RangeRequested_NotSatisfiable(string rangeString)
        {
            // Arrange            
            var contentType = "text/plain";
            var expectedMediaType = contentType;
            var lastModified = new DateTimeOffset();
            var entityTag = new EntityTagHeaderValue("\"Etag\"");
            var byteArray = Encoding.ASCII.GetBytes("Hello World");

            var result = new FileContentResult(
                fileContents: byteArray,
                contentType: new MediaTypeHeaderValue(contentType),
                enableRangeProcessing: true,
                lastModified: lastModified,
                entityTag: entityTag);

            var httpContext = GetHttpContext();
            httpContext.Request.Headers[HeaderNames.Range] = rangeString;
            httpContext.Request.Method = "GET";
            httpContext.Response.Body = new MemoryStream();

            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            // Act
            await result.ExecuteResultAsync(actionContext);
            var httpResponse = actionContext.HttpContext.Response;
            httpResponse.Body.Seek(0, SeekOrigin.Begin);
            var streamReader = new StreamReader(httpResponse.Body);
            var body = streamReader.ReadToEndAsync().Result;
            var contentRange = new ContentRangeHeaderValue(byteArray.Length);

            // Assert
            Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, httpResponse.StatusCode);
            Assert.Empty(httpResponse.Headers[HeaderNames.AcceptRanges]);
            Assert.Equal(contentRange.ToString(), httpResponse.Headers[HeaderNames.ContentRange]);
            Assert.Empty(httpResponse.Headers[HeaderNames.LastModified]);
            Assert.Empty(httpResponse.Headers[HeaderNames.ETag]);
            Assert.Equal(byteArray.Length, httpResponse.ContentLength);
            Assert.Empty(body);
        }

        [Fact]
        public async Task ExecuteResultAsync_SetsSuppliedContentTypeAndEncoding()
        {
            // Arrange
            var expectedContentType = "text/foo; charset=us-ascii";
            var buffer = new byte[] { 1, 2, 3, 4, 5 };

            var httpContext = GetHttpContext();

            var outStream = new MemoryStream();
            httpContext.Response.Body = outStream;

            var context = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            var result = new FileContentResult(buffer, expectedContentType);

            // Act
            await result.ExecuteResultAsync(context);

            // Assert
            Assert.Equal(buffer, outStream.ToArray());
            Assert.Equal(expectedContentType, httpContext.Response.ContentType);
        }

        private static IServiceCollection CreateServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton<FileContentResultExecutor>();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

            return services;
        }

        private static HttpContext GetHttpContext()
        {
            var services = CreateServices();

            var httpContext = new DefaultHttpContext();
            httpContext.RequestServices = services.BuildServiceProvider();

            return httpContext;
        }
    }
}