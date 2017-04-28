// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
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

        protected (RangeItemHeaderValue range, long rangeLength) SetHeadersAndLog(ActionContext context, FileResult result, long fileLength, DateTimeOffset? lastModified = null, EntityTagHeaderValue etag = null)
        {
            SetContentType(context, result);
            SetContentDispositionHeader(context, result);
            Logger.FileResultExecuting(result.FileDownloadName);
            SetAcceptRangeHeader(context);
            var httpRequestHeaders = context.HttpContext.Request.GetTypedHeaders();
            var httpResponseHeaders = context.HttpContext.Response.GetTypedHeaders();
            httpResponseHeaders.LastModified = lastModified;
            httpResponseHeaders.ETag = etag;
            var preconditionState = CheckPreconditionHeaders(
                    context,
                    httpRequestHeaders,
                    lastModified,
                    etag);
            if (context.HttpContext.Request.Headers.ContainsKey(HeaderNames.Range))
            {
                if (preconditionState.Equals(PreconditionState.Unspecified) ||
                    preconditionState.Equals(PreconditionState.ShouldProcess))
                    return SetRangeHeaders(context, httpRequestHeaders, fileLength, lastModified, etag);
            }
            else if (HttpMethods.IsHead(context.HttpContext.Request.Method) &&
                preconditionState.Equals(PreconditionState.ShouldProcess))
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            }

            if (preconditionState.Equals(PreconditionState.NotModified))
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status304NotModified;
            }
            if (preconditionState.Equals(PreconditionState.PreconditionFailed))
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            }

            return (null, 0);
        }

        private void RequestMethodHead()
        {
            throw new NotImplementedException();
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
            PreconditionState max = PreconditionState.Unspecified;
            for (int i = 0; i < states.Length; i++)
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
            long fileLength,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            var response = context.HttpContext.Response;
            var httpResponseHeaders = response.GetTypedHeaders();
            var range = ParseRange(context, httpRequestHeaders, fileLength, lastModified, etag);
            var rangeNotSatisfiable = range == null;
            if (rangeNotSatisfiable)
            {
                // 14.16 Content-Range - A server sending a response with status code 416 (Requested range not satisfiable)
                // SHOULD include a Content-Range field with a byte-range-resp-spec of "*". The instance-length specifies
                // the current length of the selected resource.  e.g. */length
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(fileLength);
                response.ContentLength = fileLength;
                return (null, fileLength);
            }

            httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(
                range.From.Value,
                range.To.Value,
                fileLength);

            response.StatusCode = StatusCodes.Status206PartialContent;
            var rangeLength = SetContentLength(context, range);
            return (range, rangeLength);
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
    }
}
