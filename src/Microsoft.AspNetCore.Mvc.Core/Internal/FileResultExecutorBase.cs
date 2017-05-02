// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileResultExecutorBase
    {
        private const string AcceptRangeHeaderValue = "bytes";

        public FileResultExecutorBase(ILogger logger)
        {
            Logger = logger;
        }

        internal enum PreconditionState
        {
            Unspecified,
            NotModified,
            ShouldProcess,
            PreconditionFailed,
        }

        protected ILogger Logger { get; }

        protected (RangeItemHeaderValue range, long rangeLength, bool serveBody) SetHeadersAndLog(ActionContext context, FileResult result, long? fileLength, DateTimeOffset? lastModified = null, EntityTagHeaderValue etag = null)
        {
            SetContentType(context, result);
            SetContentDispositionHeader(context, result);
            Logger.FileResultExecuting(result.FileDownloadName);
            if (fileLength.HasValue)
            {
                SetAcceptRangeHeader(context);
            }

            var request = context.HttpContext.Request;
            var httpRequestHeaders = request.GetTypedHeaders();
            var response = context.HttpContext.Response;
            var httpResponseHeaders = response.GetTypedHeaders();
            if (lastModified.HasValue)
            {
                httpResponseHeaders.LastModified = lastModified;
            }
            if (etag != null)
            {
                httpResponseHeaders.ETag = etag;
            }

            var preconditionState = CheckPreconditionHeaders(
                    context,
                    httpRequestHeaders,
                    lastModified,
                    etag);
            var serveBody = true;
            if (HttpMethods.IsHead(request.Method))
            {
                serveBody = false;
            }
            else
            {
                if (request.Headers.ContainsKey(HeaderNames.Range))
                {
                    if (preconditionState.Equals(PreconditionState.Unspecified) ||
                        preconditionState.Equals(PreconditionState.ShouldProcess))
                    {
                        var rangeInfo = SetRangeHeaders(context, httpRequestHeaders, fileLength, lastModified, etag);
                        if (response.StatusCode == StatusCodes.Status416RangeNotSatisfiable)
                        {
                            serveBody = false;
                        }
                        return (rangeInfo.range, rangeInfo.rangeLength, serveBody);
                    }
                }

                if (preconditionState.Equals(PreconditionState.NotModified))
                {
                    serveBody = false;
                    response.StatusCode = StatusCodes.Status304NotModified;
                }
                else if (preconditionState.Equals(PreconditionState.PreconditionFailed))
                {
                    serveBody = false;
                    response.StatusCode = StatusCodes.Status412PreconditionFailed;
                }
            }

            return (null, 0, serveBody);
        }

        private void SetContentType(ActionContext context, FileResult result)
        {
            var response = context.HttpContext.Response;
            response.ContentType = result.ContentType;
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

        private void SetAcceptRangeHeader(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.Headers[HeaderNames.AcceptRanges] = AcceptRangeHeaderValue;
        }

        // Internal for testing
        internal static PreconditionState CheckPreconditionHeaders(
            ActionContext context,
            RequestHeaders httpRequestHeaders,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            var ifMatchState = PreconditionState.Unspecified;
            var ifNoneMatchState = PreconditionState.Unspecified;
            var ifModifiedSinceState = PreconditionState.Unspecified;
            var ifUnmodifiedSinceState = PreconditionState.Unspecified;

            // 14.24 If-Match
            var ifMatch = httpRequestHeaders.IfMatch;
            if (ifMatch != null && ifMatch.Any())
            {
                ifMatchState = PreconditionState.PreconditionFailed;
                foreach (var entityTag in ifMatch)
                {
                    if (entityTag.Equals(EntityTagHeaderValue.Any) || entityTag.Compare(etag, useStrongComparison: true))
                    {
                        ifMatchState = PreconditionState.ShouldProcess;
                        break;
                    }
                }
            }

            // 14.26 If-None-Match
            var ifNoneMatch = httpRequestHeaders.IfNoneMatch;
            if (ifNoneMatch != null && ifNoneMatch.Any())
            {
                ifNoneMatchState = PreconditionState.ShouldProcess;
                foreach (var entityTag in ifNoneMatch)
                {
                    if (entityTag.Equals(EntityTagHeaderValue.Any) || entityTag.Compare(etag, useStrongComparison: true))
                    {
                        ifNoneMatchState = PreconditionState.NotModified;
                        break;
                    }
                }
            }

            var now = DateTimeOffset.UtcNow;

            // 14.25 If-Modified-Since
            var ifModifiedSince = httpRequestHeaders.IfModifiedSince;
            if (ifModifiedSince.HasValue && ifModifiedSince <= now)
            {
                var modified = ifModifiedSince < lastModified;
                ifModifiedSinceState = modified ? PreconditionState.ShouldProcess : PreconditionState.NotModified;
            }

            // 14.28 If-Unmodified-Since
            var ifUnmodifiedSince = httpRequestHeaders.IfUnmodifiedSince;
            if (ifUnmodifiedSince.HasValue && ifUnmodifiedSince <= now)
            {
                var unmodified = ifUnmodifiedSince >= lastModified;
                ifUnmodifiedSinceState = unmodified ? PreconditionState.ShouldProcess : PreconditionState.PreconditionFailed;
            }

            var state = GetMaxPreconditionState(ifMatchState, ifNoneMatchState, ifModifiedSinceState, ifUnmodifiedSinceState);
            return state;
        }

        private static PreconditionState GetMaxPreconditionState(params PreconditionState[] states)
        {
            var max = PreconditionState.Unspecified;
            for (var i = 0; i < states.Length; i++)
            {
                if (states[i] > max)
                {
                    max = states[i];
                }
            }
            return max;
        }

        private (RangeItemHeaderValue range, long rangeLength) SetRangeHeaders(
            ActionContext context,
            RequestHeaders httpRequestHeaders,
            long? fileLength,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            var response = context.HttpContext.Response;
            var httpResponseHeaders = response.GetTypedHeaders();
            var range = fileLength == null ? null : ParseRange(context, httpRequestHeaders, fileLength.Value, lastModified, etag);
            var rangeNotSatisfiable = range == null;
            if (rangeNotSatisfiable)
            {
                // 14.16 Content-Range - A server sending a response with status code 416 (Requested range not satisfiable)
                // SHOULD include a Content-Range field with a byte-range-resp-spec of "*". The instance-length specifies
                // the current length of the selected resource.  e.g. */length
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                if (fileLength.HasValue)
                {
                    httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(fileLength.Value);
                }
                return (null, fileLength.Value);
            }

            httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(
                range.From.Value,
                range.To.Value,
                fileLength.Value);

            response.StatusCode = StatusCodes.Status206PartialContent;
            var rangeLength = SetContentLength(context, range);
            return (range, rangeLength);
        }

        private long SetContentLength(ActionContext context, RangeItemHeaderValue range)
        {
            var start = range.From.Value;
            var end = range.To.Value;
            var length = end - start + 1;
            var response = context.HttpContext.Response;
            response.ContentLength = length;
            return length;
        }

        private RangeItemHeaderValue ParseRange(
            ActionContext context,
            RequestHeaders httpRequestHeaders,
            long fileLength,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            var httpContext = context.HttpContext;
            var response = httpContext.Response;

            var range = RangeHelper.ParseRange(
                httpContext,
                httpRequestHeaders,
                lastModified,
                etag);

            if (range != null)
            {
                var rangeValue = range.Single();
                long? from = rangeValue.From.HasValue ? rangeValue.From.Value : 0;
                long? to = rangeValue.To.HasValue ? rangeValue.To.Value : fileLength;
                var rangeItemHeaderValue = new RangeItemHeaderValue(from, to);
                var ranges = new List<RangeItemHeaderValue>
                {
                    rangeItemHeaderValue,
                };
                var normalizedRanges = RangeHelper.NormalizeRanges(ranges, fileLength);
                if (normalizedRanges == null || normalizedRanges == Array.Empty<RangeItemHeaderValue>())
                {
                    return null;
                }

                return normalizedRanges.Single();
            }

            return null;
        }

        protected static ILogger CreateLogger<T>(ILoggerFactory factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            return factory.CreateLogger<T>();
        }

        protected static async Task WriteFileAsync(HttpContext context, Stream fileStream, RangeItemHeaderValue range, long rangeLength)
        {
            var BufferSize = 0x1000;
            var outputStream = context.Response.Body;
            using (fileStream)
            {
                if (range == null)
                {
                    try
                    {
                        await StreamCopyOperation.CopyToAsync(fileStream, outputStream, null, BufferSize, context.RequestAborted);
                    }

                    catch (OperationCanceledException)
                    {
                        // Don't throw this exception, it's most likely caused by the client disconnecting.
                        // However, if it was cancelled for any other reason we need to prevent empty responses.
                        context.Abort();
                    }
                }

                else
                {
                    try
                    {
                        fileStream.Seek(range.From.Value, SeekOrigin.Begin);
                        await StreamCopyOperation.CopyToAsync(fileStream, outputStream, rangeLength, BufferSize, context.RequestAborted);
                    }

                    catch (OperationCanceledException)
                    {
                        // Don't throw this exception, it's most likely caused by the client disconnecting.
                        // However, if it was cancelled for any other reason we need to prevent empty responses.
                        context.Abort();
                    }
                }
            }
        }
    }
}
