// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileStreamResultExecutor : FileResultExecutorBase
    {
        // default buffer size as defined in BufferedStream type
        private const int BufferSize = 0x1000;

        public FileStreamResultExecutor(ILoggerFactory loggerFactory)
            : base(CreateLogger<VirtualFileResultExecutor>(loggerFactory))
        {
        }

        public Task ExecuteAsync(ActionContext context, FileStreamResult result)
        {
            RangeItemHeaderValue range = new RangeItemHeaderValue(0, 0);
            long rangeLength = default(long);
            if (result.EnableRangeProcessing)
            {
                range = SetContentRangeAndStatusCode(context, result);
                rangeLength = (range == null) ? 0: SetRangeHeaders(context, result, range);
            }

            SetHeadersAndLog(context, result);
            return WriteFileAsync(context, result, range, rangeLength);
        }

        private static async Task WriteFileAsync(ActionContext context, FileStreamResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;

            using (result.FileStream)
            {
                if (!result.EnableRangeProcessing)
                {
                    await result.FileStream.CopyToAsync(outputStream, BufferSize);
                }

                else if (range == null || rangeLength == 0)
                {
                    return;
                }

                else
                {
                    try
                    {
                        result.FileStream.Seek(range.From.Value, SeekOrigin.Begin);
                        await StreamCopyOperation.CopyToAsync(result.FileStream, outputStream, rangeLength, context.HttpContext.RequestAborted);
                    }

                    catch (OperationCanceledException)
                    {                       
                        // Don't throw this exception, it's most likely caused by the client disconnecting.
                        // However, if it was cancelled for any other reason we need to prevent empty responses.
                        context.HttpContext.Abort();
                    }
                }
            }
        }

        private RangeItemHeaderValue SetContentRangeAndStatusCode(ActionContext context, FileStreamResult result)
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
                httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(result.FileStream.Length);
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.ContentLength = result.FileStream.Length;
                return null;
            }

            // Multi-range is not supported.
            Debug.Assert(ranges.Count == 1);

            var range = ranges.SingleOrDefault();
            httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(
                range.From.Value,
                range.To.Value,
                result.FileStream.Length);

            httpResponseHeaders.LastModified = result.LastModified;
            httpResponseHeaders.ETag = result.EntityTag;
            response.StatusCode = StatusCodes.Status206PartialContent;
            return range;
        }

        private ICollection<RangeItemHeaderValue> ParseRange(ActionContext context, FileStreamResult result)
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
                var normalizedRanges = RangeHelper.NormalizeRanges(range, result.FileStream.Length);
                return normalizedRanges;
            }

            return null;
        }
    }
}
