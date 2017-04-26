// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
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

        protected ILogger Logger { get; }

        protected (RangeItemHeaderValue range, long rangeLength) SetHeadersAndLog(ActionContext context, FileResult result, long fileLength, DateTimeOffset? lastModified = null, EntityTagHeaderValue etag = null)
        {
            SetContentType(context, result);
            SetContentDispositionHeader(context, result);
            Logger.FileResultExecuting(result.FileDownloadName);
            SetAcceptRangeHeader(context);
            var httpRequestHeaders = context.HttpContext.Request.GetTypedHeaders();
            if (context.HttpContext.Request.Headers.ContainsKey(HeaderNames.Range))
            {
                bool shouldProcess = ComputeConditionalRequestHeaders(
                    context,
                    httpRequestHeaders,
                    lastModified,
                    etag);

                if (shouldProcess)
                {
                    return SetRangeHeaders(context, httpRequestHeaders, fileLength, lastModified, etag);
                }
            }

            return (null, 0);
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
        internal static bool ComputeConditionalRequestHeaders(
            ActionContext context,
            RequestHeaders httpRequestHeaders,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            // 14.24 If-Match
            bool shouldProcess = false;
            var ifMatch = httpRequestHeaders.IfMatch;
            var ifNoneMatch = httpRequestHeaders.IfNoneMatch;
            var ifModifiedSince = httpRequestHeaders.IfModifiedSince;
            var ifUnmodifiedSince = httpRequestHeaders.IfUnmodifiedSince;

            if (ifMatch == null && ifNoneMatch == null && !ifModifiedSince.HasValue && !ifUnmodifiedSince.HasValue)
            {
                return true;
            }

            if (ifMatch != null && etag != null && ifMatch.Any())
            {
                foreach (var entityTag in ifMatch)
                {
                    if (entityTag.Equals(EntityTagHeaderValue.Any) || entityTag.Compare(etag, useStrongComparison: true))
                    {
                        shouldProcess = true;
                        break;
                    }
                }
                if (!shouldProcess)
                {
                    PreconditionFailed(context);
                    return false;
                }
            }

            else
            {
                // 14.28 If-Unmodified-Since
                var now = DateTimeOffset.UtcNow;
                if (ifUnmodifiedSince.HasValue && lastModified.HasValue && ifUnmodifiedSince <= now)
                {
                    bool unmodified = ifUnmodifiedSince >= lastModified;
                    shouldProcess = unmodified ? true : false;
                }
                if (!shouldProcess)
                {
                    PreconditionFailed(context);
                    return false;
                }
            }

            var method = context.HttpContext.Request.Method;
            if (shouldProcess)
            {
                // 14.26 If-None-Match
                if (ifNoneMatch != null && etag != null && ifNoneMatch.Any())
                {
                    foreach (var entityTag in ifNoneMatch)
                    {
                        if (entityTag.Equals(EntityTagHeaderValue.Any) || entityTag.Compare(etag, useStrongComparison: true))
                        {
                            shouldProcess = false;
                            break;
                        }
                    }
                }

                else if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
                {
                    // 14.25 If-Modified-Since
                    var now = DateTimeOffset.UtcNow;
                    if (ifModifiedSince.HasValue && lastModified.HasValue && ifModifiedSince <= now)
                    {
                        bool modified = ifModifiedSince < lastModified;
                        shouldProcess = modified ? true : false;
                    }
                }
            }

            if (!shouldProcess)
            {
                if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
                {
                    NotModified(context);
                }
                else
                {
                    PreconditionFailed(context);
                }
            }
            return shouldProcess;
        }

        private static void PreconditionFailed(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status412PreconditionFailed;
        }

        private static void NotModified(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status304NotModified;
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
            httpResponseHeaders.LastModified = lastModified;
            httpResponseHeaders.ETag = etag;

            var range = ParseRange(context, httpRequestHeaders, fileLength, lastModified, etag);
            bool rangeNotSatisfiable = range == null;
            if (rangeNotSatisfiable)
            {
                // 14.16 Content-Range - A server sending a response with status code 416 (Requested range not satisfiable)
                // SHOULD include a Content-Range field with a byte-range-resp-spec of "*". The instance-length specifies
                // the current length of the selected resource.  e.g. */length
                httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(fileLength);
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
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
                var normalizedRanges = RangeHelper.NormalizeRanges(range, fileLength);
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
