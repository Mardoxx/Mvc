// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileContentResultExecutor : FileResultExecutorBase
    {
        public FileContentResultExecutor(ILoggerFactory loggerFactory)
            : base(CreateLogger<FileContentResultExecutor>(loggerFactory))
        {
        }

        public Task ExecuteAsync(ActionContext context, FileContentResult result)
        {
            RangeItemHeaderValue range = new RangeItemHeaderValue(0, 0);
            long rangeLength = default(long);
            if (result.EnableRangeProcessing)
            {
                range = SetContentRangeAndStatusCode(context, result);
                rangeLength = (range == null) ? 0 : SetRangeHeaders(context, result, range);
            }

            SetHeadersAndLog(context, result);
            return WriteFileAsync(context, result, range, rangeLength);
        }

        private static Task WriteFileAsync(ActionContext context, FileContentResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;

            if (!result.EnableRangeProcessing)
            {
                return response.Body.WriteAsync(result.FileContents, offset: 0, count: result.FileContents.Length);
            }

            else if (range == null || rangeLength == 0)
            {
                return Task.CompletedTask;
            }

            else
            {
                try
                {
                    return response.Body.WriteAsync(result.FileContents, offset: (int)range.From.Value, count: (int)rangeLength);
                }

                catch (OperationCanceledException ex)
                {
                    // Don't throw this exception, it's most likely caused by the client disconnecting.
                    // However, if it was cancelled for any other reason we need to prevent empty responses.
                    context.HttpContext.Abort();
                    return Task.FromException(ex);
                }

            }
        }

        private RangeItemHeaderValue SetContentRangeAndStatusCode(ActionContext context, FileContentResult result)
        {
            var ranges = ParseRange(context, result);
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
                httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(result.FileContents.Length);
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.ContentLength = result.FileContents.Length;
                return null;
            }

            // Multi-range is not supported.
            Debug.Assert(ranges.Count == 1);

            var range = ranges.SingleOrDefault();
            httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(
                range.From.Value,
                range.To.Value,
                result.FileContents.Length);

            httpResponseHeaders.LastModified = result.LastModified;
            httpResponseHeaders.ETag = result.EntityTag;
            response.StatusCode = StatusCodes.Status206PartialContent;
            return range;
        }

        private ICollection<RangeItemHeaderValue> ParseRange(ActionContext context, FileContentResult result)
        {
            var httpContext = context.HttpContext;
            var httpRequestHeaders = httpContext.Request.GetTypedHeaders();
            var response = httpContext.Response;

            var lastModified = result.LastModified;
            var etag = result.EntityTag;

            var range = RangeHelper.ParseRange(
                context: httpContext,
                requestHeaders: httpRequestHeaders,
                lastModified: lastModified,
                etag: etag);

            if (range?.Count == 1)
            {
                var normalizedRanges = RangeHelper.NormalizeRanges(range, result.FileContents.Length);
                return normalizedRanges;
            }

            return null;
        }
    }
}
