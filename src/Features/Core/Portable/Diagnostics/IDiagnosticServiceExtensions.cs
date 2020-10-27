﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    internal static class IDiagnosticServiceExtensions
    {
        public static ImmutableArray<DiagnosticData> GetPullDiagnostics(this IDiagnosticService service, DiagnosticBucket bucket, bool includeSuppressedDiagnostics, Option2<bool> pullDiagnosticOption, CancellationToken cancellationToken)
            => service.GetPullDiagnostics(bucket.Workspace, bucket.ProjectId, bucket.DocumentId, bucket.Id, includeSuppressedDiagnostics, pullDiagnosticOption, cancellationToken);

        public static ImmutableArray<DiagnosticData> GetPushDiagnostics(this IDiagnosticService service, DiagnosticBucket bucket, bool includeSuppressedDiagnostics, Option2<bool> pullDiagnosticOption, CancellationToken cancellationToken)
            => service.GetPushDiagnostics(bucket.Workspace, bucket.ProjectId, bucket.DocumentId, bucket.Id, includeSuppressedDiagnostics, pullDiagnosticOption, cancellationToken);

        public static ImmutableArray<DiagnosticData> GetPushDiagnostics(
            this IDiagnosticService service,
            Document document,
            bool includeSuppressedDiagnostics,
            Option2<bool> pullDiagnosticsOption,
            CancellationToken cancellationToken)
        {
            return GetDiagnostics(service, document, includeSuppressedDiagnostics, forPullDiagnostics: false, pullDiagnosticsOption, cancellationToken);
        }

        public static ImmutableArray<DiagnosticData> GetPullDiagnostics(
            this IDiagnosticService service,
            Document document,
            bool includeSuppressedDiagnostics,
            Option2<bool> pullDiagnosticsOption,
            CancellationToken cancellationToken)
        {
            return GetDiagnostics(service, document, includeSuppressedDiagnostics, forPullDiagnostics: true, pullDiagnosticsOption, cancellationToken);
        }

        public static ImmutableArray<DiagnosticData> GetDiagnostics(
            this IDiagnosticService service,
            Document document,
            bool includeSuppressedDiagnostics,
            bool forPullDiagnostics,
            Option2<bool> pullDiagnosticsOption,
            CancellationToken cancellationToken)
        {
            var project = document.Project;
            var workspace = project.Solution.Workspace;

            using var _ = ArrayBuilder<DiagnosticData>.GetInstance(out var result);

            var buckets = forPullDiagnostics
                ? service.GetPullDiagnosticBuckets(workspace, project.Id, document.Id, pullDiagnosticsOption, cancellationToken)
                : service.GetPushDiagnosticBuckets(workspace, project.Id, document.Id, pullDiagnosticsOption, cancellationToken);

            foreach (var bucket in buckets)
            {
                Contract.ThrowIfFalse(workspace.Equals(bucket.Workspace));
                Contract.ThrowIfFalse(document.Id.Equals(bucket.DocumentId));

                var diagnostics = forPullDiagnostics
                    ? service.GetPullDiagnostics(bucket, includeSuppressedDiagnostics, pullDiagnosticsOption, cancellationToken)
                    : service.GetPushDiagnostics(bucket, includeSuppressedDiagnostics, pullDiagnosticsOption, cancellationToken);
                result.AddRange(diagnostics);
            }

            return result.ToImmutable();
        }
    }
}
