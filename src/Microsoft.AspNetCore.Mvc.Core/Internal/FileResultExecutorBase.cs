// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileResultExecutorBase
    {
        public FileResultExecutorBase(ILogger logger)
        {
            Logger = logger;
        }

        protected ILogger Logger { get; }

        protected void SetHeadersAndLog(ActionContext context, FileResult result)
        {
            SetContentType(context, result);
            SetContentDispositionHeader(context, result);
            Logger.FileResultExecuting(result.FileDownloadName);
        }

        private long SetContentLength(ActionContext context, RangeItemHeaderValue range)
        {
            long start = range.From.Value;
            long end = range.To.Value;
            long length = end - start + 1;
            var response = context.HttpContext.Response;
            response.ContentLength = length;
            return length;
        }

        protected long SetRangeHeaders(ActionContext context, FileResult result, RangeItemHeaderValue range)
        {
            var method = context.HttpContext.Request.Method;
            var isGet = string.Equals("GET", method, StringComparison.OrdinalIgnoreCase);
            if (!isGet)
            {
                result.EnableRangeProcessing = false;
            }

            if (result.EnableRangeProcessing)
            {
                var response = context.HttpContext.Response;
                response.Headers[HeaderNames.AcceptRanges] = "bytes";
                long rangeLength = SetContentLength(context, range);
                return rangeLength;
            }

            return default(long);
        }

        protected RangeItemHeaderValue SetContentRangeAndStatusCode(
            ActionContext context,
            DateTimeOffset lastModified,
            EntityTagHeaderValue etag,
            long fileLength)
        {
            var ranges = ParseRange(context, lastModified, etag, fileLength);
            var response = context.HttpContext.Response;
            var httpResponseHeaders = response.GetTypedHeaders();
            bool rangeNotSatisfiable = false;
            if (ranges == null || ranges.Count != 1)
            {
                rangeNotSatisfiable = true;
            }

            if (rangeNotSatisfiable)
            {
                // 14.16 Content-Range - A server sending a response with status code 416 (Requested range not satisfiable)
                // SHOULD include a Content-Range field with a byte-range-resp-spec of "*". The instance-length specifies
                // the current length of the selected resource.  e.g. */length
                httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(fileLength);
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.ContentLength = fileLength;
                return null;
            }

            // Multi-range is not supported.
            Debug.Assert(ranges.Count == 1);

            var range = ranges.SingleOrDefault();
            httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(
                range.From.Value,
                range.To.Value,
                fileLength);

            httpResponseHeaders.LastModified = lastModified;
            httpResponseHeaders.ETag = etag;
            response.StatusCode = StatusCodes.Status206PartialContent;
            return range;
        }

        private ICollection<RangeItemHeaderValue> ParseRange(
            ActionContext context,
            DateTimeOffset lastModified,
            EntityTagHeaderValue etag,
            long fileLength)
        {
            var httpContext = context.HttpContext;
            var httpRequestHeaders = httpContext.Request.GetTypedHeaders();
            var response = httpContext.Response;

            var range = RangeHelper.ParseRange(
                httpContext,
                httpRequestHeaders,
                lastModified,
                etag);

            if (range?.Count == 1)
            {
                var normalizedRanges = RangeHelper.NormalizeRanges(range, fileLength);
                return normalizedRanges;
            }

            return null;
        }

        private void SetContentDispositionHeader(ActionContext context, FileResult result)
        {
            if (!string.IsNullOrEmpty(result.FileDownloadName))
            {
                // From RFC 2183, Sec. 2.3:
                // The sender may want to suggest a filename to be used if the entity is
                // detached and stored in a separate file. If the receiving MUA writes
                // the entity to a file, the suggested filename should be used as a
                // basis for the actual filename, where possible.
                var contentDisposition = new ContentDispositionHeaderValue("attachment");
                contentDisposition.SetHttpFileName(result.FileDownloadName);
                context.HttpContext.Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
            }
        }

        private void SetContentType(ActionContext context, FileResult result)
        {
            var response = context.HttpContext.Response;
            response.ContentType = result.ContentType;
        }

        protected static ILogger CreateLogger<T>(ILoggerFactory factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            return factory.CreateLogger<T>();
        }
    }
}
