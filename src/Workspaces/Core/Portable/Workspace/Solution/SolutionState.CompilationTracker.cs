﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Logging;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    internal partial class SolutionState
    {
        /// <summary>
        /// Tracks the changes made to a project and provides the facility to get a lazily built
        /// compilation for that project.  As the compilation is being built, the partial results are
        /// stored as well so that they can be used in the 'in progress' workspace snapshot.
        /// </summary>
        private partial class CompilationTracker
        {
            private static readonly Func<ProjectState, string> s_logBuildCompilationAsync =
                state => string.Join(",", state.AssemblyName, state.DocumentIds.Count);

            public ProjectState ProjectState { get; }

            /// <summary>
            /// Access via the <see cref="ReadState"/> and <see cref="WriteState"/> methods.
            /// </summary>
            private State _stateDoNotAccessDirectly;

            // guarantees only one thread is building at a time
            private readonly SemaphoreSlim _buildLock = new(initialCount: 1);

            private CompilationTracker(
                ProjectState project,
                State state)
            {
                Contract.ThrowIfNull(project);

                this.ProjectState = project;
                _stateDoNotAccessDirectly = state;
            }

            /// <summary>
            /// Creates a tracker for the provided project.  The tracker will be in the 'empty' state
            /// and will have no extra information beyond the project itself.
            /// </summary>
            public CompilationTracker(ProjectState project)
                : this(project, State.Empty)
            {
            }

            private State ReadState()
                => Volatile.Read(ref _stateDoNotAccessDirectly);

            private void WriteState(State state, SolutionServices solutionServices)
            {
                if (solutionServices.SupportsCachingRecoverableObjects)
                {
                    // Allow the cache service to create a strong reference to the compilation. We'll get the "furthest along" compilation we have
                    // and hold onto that.
                    var compilationToCache = state.FinalCompilationWithGeneratedDocuments?.GetValueOrNull() ?? state.CompilationWithoutGeneratedDocuments?.GetValueOrNull();
                    solutionServices.CacheService.CacheObjectIfCachingEnabledForKey(ProjectState.Id, state, compilationToCache);
                }

                Volatile.Write(ref _stateDoNotAccessDirectly, state);
            }

            /// <summary>
            /// Returns true if this tracker currently either points to a compilation, has an in-progress
            /// compilation being computed, or has a skeleton reference.  Note: this is simply a weak
            /// statement about the tracker at this exact moment in time.  Immediately after this returns
            /// the tracker might change and may no longer have a final compilation (for example, if the
            /// retainer let go of it) or might not have an in-progress compilation (for example, if the
            /// background compiler finished with it).
            /// 
            /// Because of the above limitations, this should only be used by clients as a weak form of
            /// information about the tracker.  For example, a client may see that a tracker has no
            /// compilation and may choose to throw it away knowing that it could be reconstructed at a
            /// later point if necessary.
            /// </summary>
            public bool HasCompilation
            {
                get
                {
                    var state = this.ReadState();
                    return state.CompilationWithoutGeneratedDocuments != null && state.CompilationWithoutGeneratedDocuments.TryGetValue(out _) || state.DeclarationOnlyCompilation != null;
                }
            }

            /// <summary>
            /// Returns <see langword="true"/> if this <see cref="Project"/>/<see cref="Compilation"/> could produce the
            /// given <paramref name="symbol"/>.  The symbol must be a <see cref="IAssemblySymbol"/>, <see
            /// cref="IModuleSymbol"/> or <see cref="IDynamicTypeSymbol"/>.
            /// </summary>
            /// <remarks>
            /// If <paramref name="primary"/> is true, then <see cref="Compilation.References"/> will not be considered
            /// when answering this question.  In other words, if <paramref name="symbol"/>  is an <see
            /// cref="IAssemblySymbol"/> and <paramref name="primary"/> is <see langword="true"/> then this will only
            /// return true if the symbol is <see cref="Compilation.Assembly"/>.  If <paramref name="primary"/> is
            /// false, then it can return true if <paramref name="symbol"/> is <see cref="Compilation.Assembly"/> or any
            /// of the symbols returned by <see cref="Compilation.GetAssemblyOrModuleSymbol(MetadataReference)"/> for
            /// any of the references of the <see cref="Compilation.References"/>.
            /// </remarks>
            public bool ContainsAssemblyOrModuleOrDynamic(ISymbol symbol, bool primary)
            {
                Debug.Assert(symbol.Kind == SymbolKind.Assembly ||
                             symbol.Kind == SymbolKind.NetModule ||
                             symbol.Kind == SymbolKind.DynamicType);
                var state = this.ReadState();

                var unrootedSymbolSet = state.UnrootedSymbolSet;
                if (unrootedSymbolSet == null)
                {
                    // this was not a tracker that hands out symbols (for example, it's a 'declaration table only'
                    // tracker).  So we have nothing to check this symbol against.
                    return false;
                }

                if (primary)
                {
                    return symbol.Equals(unrootedSymbolSet.Value.PrimaryAssemblySymbol.GetTarget()) ||
                           symbol.Equals(unrootedSymbolSet.Value.PrimaryDynamicSymbol.GetTarget());
                }
                else
                {
                    return unrootedSymbolSet.Value.SecondaryReferencedSymbols.Contains(symbol);
                }
            }

            /// <summary>
            /// Creates a new instance of the compilation info, retaining any already built
            /// compilation state as the now 'old' state
            /// </summary>
            public CompilationTracker Fork(
                ProjectState newProject,
                CompilationAndGeneratorDriverTranslationAction? translate = null,
                bool clone = false,
                CancellationToken cancellationToken = default)
            {
                var state = ReadState();

                var baseCompilation = state.CompilationWithoutGeneratedDocuments?.GetValueOrNull(cancellationToken);
                if (baseCompilation != null)
                {
                    // We have some pre-calculated state to incrementally update
                    var newInProgressCompilation = clone
                        ? baseCompilation.Clone()
                        : baseCompilation;

                    var intermediateProjects = state is InProgressState inProgressState
                        ? inProgressState.IntermediateProjects
                        : ImmutableArray.Create<(ProjectState, CompilationAndGeneratorDriverTranslationAction)>();

                    var newIntermediateProjects = translate == null
                         ? intermediateProjects
                         : intermediateProjects.Add((ProjectState, translate));

                    var newState = State.Create(newInProgressCompilation, state.GeneratedDocuments, newIntermediateProjects);

                    return new CompilationTracker(newProject, newState);
                }

                var declarationOnlyCompilation = state.DeclarationOnlyCompilation;
                if (declarationOnlyCompilation != null)
                {
                    if (translate != null)
                    {
                        var intermediateProjects = ImmutableArray.Create((this.ProjectState, translate));
                        return new CompilationTracker(newProject, new InProgressState(declarationOnlyCompilation, state.GeneratedDocuments, intermediateProjects));
                    }

                    return new CompilationTracker(newProject, new LightDeclarationState(declarationOnlyCompilation, state.GeneratedDocuments, generatedDocumentsAreFinal: false));
                }

                // We have nothing.  Just make a tracker that only points to the new project.  We'll have
                // to rebuild its compilation from scratch if anyone asks for it.
                return new CompilationTracker(newProject);
            }

            /// <summary>
            /// Creates a fork with the same final project.
            /// </summary>
            public CompilationTracker Clone()
                => this.Fork(this.ProjectState, clone: true);

            public CompilationTracker FreezePartialStateWithTree(SolutionState solution, DocumentState docState, SyntaxTree tree, CancellationToken cancellationToken)
            {
                GetPartialCompilationState(solution, docState.Id, out var inProgressProject, out var inProgressCompilation, out var sourceGeneratedDocuments, cancellationToken);

                if (!inProgressCompilation.SyntaxTrees.Contains(tree))
                {
                    var existingTree = inProgressCompilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == tree.FilePath);
                    if (existingTree != null)
                    {
                        inProgressCompilation = inProgressCompilation.ReplaceSyntaxTree(existingTree, tree);
                        inProgressProject = inProgressProject.UpdateDocument(docState, textChanged: false, recalculateDependentVersions: false);
                    }
                    else
                    {
                        inProgressCompilation = inProgressCompilation.AddSyntaxTrees(tree);
                        Debug.Assert(!inProgressProject.DocumentIds.Contains(docState.Id));
                        inProgressProject = inProgressProject.AddDocuments(ImmutableArray.Create(docState));
                    }
                }

                // The user is asking for an in progress snap.  We don't want to create it and then
                // have the compilation immediately disappear.  So we force it to stay around with a ConstantValueSource.
                // As a policy, all partial-state projects are said to have incomplete references, since the state has no guarantees.
                return new CompilationTracker(
                    inProgressProject,
                    new FinalState(
                        new ConstantValueSource<Optional<Compilation>>(inProgressCompilation),
                        new ConstantValueSource<Optional<Compilation>>(inProgressCompilation),
                        inProgressCompilation,
                        hasSuccessfullyLoaded: false,
                        sourceGeneratedDocuments,
                        State.GetUnrootedSymbols(inProgressCompilation)));
            }

            /// <summary>
            /// Tries to get the latest snapshot of the compilation without waiting for it to be
            /// fully built. This method takes advantage of the progress side-effect produced during
            /// <see cref="BuildCompilationInfoAsync(SolutionState, CancellationToken)"/>.
            /// It will either return the already built compilation, any
            /// in-progress compilation or any known old compilation in that order of preference.
            /// The compilation state that is returned will have a compilation that is retained so
            /// that it cannot disappear.
            /// </summary>
            /// <param name="inProgressCompilation">The compilation to return. Contains any source generated documents that were available already added.</param>
            private void GetPartialCompilationState(
                SolutionState solution,
                DocumentId id,
                out ProjectState inProgressProject,
                out Compilation inProgressCompilation,
                out ImmutableArray<SourceGeneratedDocumentState> sourceGeneratedDocuments,
                CancellationToken cancellationToken)
            {
                var state = ReadState();
                var compilationWithoutGeneratedDocuments = state.CompilationWithoutGeneratedDocuments?.GetValueOrNull(cancellationToken);

                // check whether we can bail out quickly for typing case
                var inProgressState = state as InProgressState;

                sourceGeneratedDocuments = state.GeneratedDocuments;

                // all changes left for this document is modifying the given document.
                // we can use current state as it is since we will replace the document with latest document anyway.
                if (inProgressState != null &&
                    compilationWithoutGeneratedDocuments != null &&
                    inProgressState.IntermediateProjects.All(t => IsTouchDocumentActionForDocument(t.action, id)))
                {
                    inProgressProject = ProjectState;

                    // We'll add in whatever generated documents we do have; these may be from a prior run prior to some changes
                    // being made to the project, but it's the best we have so we'll use it.
                    inProgressCompilation = compilationWithoutGeneratedDocuments.AddSyntaxTrees(sourceGeneratedDocuments.Select(d => d.SyntaxTree));

                    SolutionLogger.UseExistingPartialProjectState();
                    return;
                }

                inProgressProject = inProgressState != null ? inProgressState.IntermediateProjects.First().state : this.ProjectState;

                // if we already have a final compilation we are done.
                if (compilationWithoutGeneratedDocuments != null && state is FinalState finalState)
                {
                    var finalCompilation = finalState.FinalCompilationWithGeneratedDocuments.GetValueOrNull(cancellationToken);

                    if (finalCompilation != null)
                    {
                        inProgressCompilation = finalCompilation;
                        SolutionLogger.UseExistingFullProjectState();
                        return;
                    }
                }

                // 1) if we have an in-progress compilation use it.  
                // 2) If we don't, then create a simple empty compilation/project. 
                // 3) then, make sure that all it's p2p refs and whatnot are correct.
                if (compilationWithoutGeneratedDocuments == null)
                {
                    inProgressProject = inProgressProject.RemoveAllDocuments();
                    inProgressCompilation = CreateEmptyCompilation();
                }
                else
                {
                    inProgressCompilation = compilationWithoutGeneratedDocuments;
                }

                inProgressCompilation = inProgressCompilation.AddSyntaxTrees(sourceGeneratedDocuments.Select(d => d.SyntaxTree));

                // Now add in back a consistent set of project references.  For project references
                // try to get either a CompilationReference or a SkeletonReference. This ensures
                // that the in-progress project only reports a reference to another project if it
                // could actually get a reference to that project's metadata.
                var metadataReferences = new List<MetadataReference>();
                var newProjectReferences = new List<ProjectReference>();
                metadataReferences.AddRange(this.ProjectState.MetadataReferences);

                var metadataReferenceToProjectId = new Dictionary<MetadataReference, ProjectId>();

                foreach (var projectReference in this.ProjectState.ProjectReferences)
                {
                    var referencedProject = solution.GetProjectState(projectReference.ProjectId);
                    if (referencedProject != null)
                    {
                        if (referencedProject.IsSubmission)
                        {
                            var previousScriptCompilation = solution.GetCompilationAsync(projectReference.ProjectId, cancellationToken).WaitAndGetResult(cancellationToken);

                            // previous submission project must support compilation:
                            RoslynDebug.Assert(previousScriptCompilation != null);

                            inProgressCompilation = inProgressCompilation.WithScriptCompilationInfo(inProgressCompilation.ScriptCompilationInfo!.WithPreviousScriptCompilation(previousScriptCompilation));
                        }
                        else
                        {
                            // get the latest metadata for the partial compilation of the referenced project.
                            var metadata = solution.GetPartialMetadataReference(projectReference, this.ProjectState);

                            if (metadata == null)
                            {
                                // if we failed to get the metadata, check to see if we previously had existing metadata and reuse it instead.
                                var inProgressCompilationNotRef = inProgressCompilation;
                                metadata = inProgressCompilationNotRef.ExternalReferences.FirstOrDefault(
                                    r => solution.GetProjectState(inProgressCompilationNotRef.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol)?.Id == projectReference.ProjectId);
                            }

                            if (metadata != null)
                            {
                                newProjectReferences.Add(projectReference);
                                metadataReferences.Add(metadata);
                                metadataReferenceToProjectId.Add(metadata, projectReference.ProjectId);
                            }
                        }
                    }
                }

                inProgressProject = inProgressProject.WithProjectReferences(newProjectReferences);

                if (!Enumerable.SequenceEqual(inProgressCompilation.ExternalReferences, metadataReferences))
                {
                    inProgressCompilation = inProgressCompilation.WithReferences(metadataReferences);
                }

                RecordAssemblySymbols(inProgressCompilation, metadataReferenceToProjectId);

                SolutionLogger.CreatePartialProjectState();
            }

            private static bool IsTouchDocumentActionForDocument(CompilationAndGeneratorDriverTranslationAction action, DocumentId id)
                => action is CompilationAndGeneratorDriverTranslationAction.TouchDocumentAction touchDocumentAction &&
                   touchDocumentAction.DocumentId == id;

            /// <summary>
            /// Gets the final compilation if it is available.
            /// </summary>
            public bool TryGetCompilation([NotNullWhen(true)] out Compilation? compilation)
            {
                var state = ReadState();
                if (state.FinalCompilationWithGeneratedDocuments != null && state.FinalCompilationWithGeneratedDocuments.TryGetValue(out var compilationOpt) && compilationOpt.HasValue)
                {
                    compilation = compilationOpt.Value;
                    return true;
                }

                compilation = null;
                return false;
            }

            public Task<Compilation> GetCompilationAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                if (this.TryGetCompilation(out var compilation))
                {
                    // PERF: This is a hot code path and Task<TResult> isn't cheap,
                    // so cache the completed tasks to reduce allocations. We also
                    // need to avoid keeping a strong reference to the Compilation,
                    // so use a ConditionalWeakTable.
                    return SpecializedTasks.FromResult(compilation);
                }
                else
                {
                    return GetCompilationSlowAsync(solution, cancellationToken);
                }
            }

            private async Task<Compilation> GetCompilationSlowAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                var compilationInfo = await GetOrBuildCompilationInfoAsync(solution, lockGate: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                return compilationInfo.Compilation;
            }

            private async Task<Compilation> GetOrBuildDeclarationCompilationAsync(SolutionServices solutionServices, CancellationToken cancellationToken)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using (await _buildLock.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var state = ReadState();

                        // we are already in the final stage. just return it.
                        var compilation = state.FinalCompilationWithGeneratedDocuments?.GetValueOrNull(cancellationToken);
                        if (compilation != null)
                        {
                            return compilation;
                        }

                        compilation = state.CompilationWithoutGeneratedDocuments?.GetValueOrNull(cancellationToken);
                        if (compilation == null)
                        {
                            // let's see whether we have declaration only compilation
                            if (state.DeclarationOnlyCompilation != null)
                            {
                                // okay, move to full declaration state. do this so that declaration only compilation never
                                // realize symbols.
                                var declarationOnlyCompilation = state.DeclarationOnlyCompilation.Clone();
                                WriteState(new FullDeclarationState(declarationOnlyCompilation, state.GeneratedDocuments, state.GeneratedDocumentsAreFinal), solutionServices);
                                return declarationOnlyCompilation;
                            }

                            // We've got nothing.  Build it from scratch :(
                            return await BuildDeclarationCompilationFromScratchAsync(solutionServices, cancellationToken).ConfigureAwait(false);
                        }

                        if (state is FullDeclarationState or FinalState)
                        {
                            // we have full declaration, just use it.
                            return compilation;
                        }

                        compilation = await BuildDeclarationCompilationFromInProgressAsync(solutionServices, (InProgressState)state, compilation, cancellationToken).ConfigureAwait(false);

                        // We must have an in progress compilation. Build off of that.
                        return compilation;
                    }
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            private async Task<CompilationInfo> GetOrBuildCompilationInfoAsync(
                SolutionState solution,
                bool lockGate,
                CancellationToken cancellationToken)
            {
                try
                {
                    using (Logger.LogBlock(FunctionId.Workspace_Project_CompilationTracker_BuildCompilationAsync,
                                           s_logBuildCompilationAsync, ProjectState, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var state = ReadState();

                        // Try to get the built compilation.  If it exists, then we can just return that.
                        var finalCompilation = state.FinalCompilationWithGeneratedDocuments?.GetValueOrNull(cancellationToken);
                        if (finalCompilation != null)
                        {
                            RoslynDebug.Assert(state.HasSuccessfullyLoaded.HasValue);
                            return new CompilationInfo(finalCompilation, state.HasSuccessfullyLoaded.Value, state.GeneratedDocuments);
                        }

                        // Otherwise, we actually have to build it.  Ensure that only one thread is trying to
                        // build this compilation at a time.
                        if (lockGate)
                        {
                            using (await _buildLock.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
                            {
                                return await BuildCompilationInfoAsync(solution, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            return await BuildCompilationInfoAsync(solution, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            /// <summary>
            /// Builds the compilation matching the project state. In the process of building, also
            /// produce in progress snapshots that can be accessed from other threads.
            /// </summary>
            private Task<CompilationInfo> BuildCompilationInfoAsync(
                SolutionState solution,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = ReadState();

                // if we already have a compilation, we must be already done!  This can happen if two
                // threads were waiting to build, and we came in after the other succeeded.
                var compilation = state.FinalCompilationWithGeneratedDocuments?.GetValueOrNull(cancellationToken);
                if (compilation != null)
                {
                    RoslynDebug.Assert(state.HasSuccessfullyLoaded.HasValue);
                    return Task.FromResult(new CompilationInfo(compilation, state.HasSuccessfullyLoaded.Value, state.GeneratedDocuments));
                }

                compilation = state.CompilationWithoutGeneratedDocuments?.GetValueOrNull(cancellationToken);

                // If we have already reached FinalState in the past but the compilation was garbage collected, we still have the generated documents
                // so we can pass those to FinalizeCompilationAsync to avoid the recomputation. This is necessary for correctness as otherwise
                // we'd be reparsing trees which could result in generated documents changing identity.
                ImmutableArray<SourceGeneratedDocumentState>? authoritativeGeneratedDocuments =
                    state.GeneratedDocumentsAreFinal ? state.GeneratedDocuments : null;

                if (compilation == null)
                {
                    // this can happen if compilation is already kicked out from the cache.
                    // check whether the state we have support declaration only compilation
                    if (state.DeclarationOnlyCompilation != null)
                    {
                        // we have declaration only compilation. build final one from it.
                        return FinalizeCompilationAsync(solution, state.DeclarationOnlyCompilation, authoritativeGeneratedDocuments, cancellationToken);
                    }

                    // We've got nothing.  Build it from scratch :(
                    return BuildCompilationInfoFromScratchAsync(solution, cancellationToken);
                }

                if (state is FullDeclarationState or FinalState)
                {
                    // We have a declaration compilation, use it to reconstruct the final compilation
                    return FinalizeCompilationAsync(solution, compilation, authoritativeGeneratedDocuments, cancellationToken);
                }
                else
                {
                    // We must have an in progress compilation. Build off of that.
                    return BuildFinalStateFromInProgressStateAsync(solution, (InProgressState)state, compilation, cancellationToken);
                }
            }

            private async Task<CompilationInfo> BuildCompilationInfoFromScratchAsync(
                SolutionState solution, CancellationToken cancellationToken)
            {
                try
                {
                    var compilation = await BuildDeclarationCompilationFromScratchAsync(solution.Services, cancellationToken).ConfigureAwait(false);

                    return await FinalizeCompilationAsync(solution, compilation, authoritativeGeneratedDocuments: null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            [PerformanceSensitive(
                "https://github.com/dotnet/roslyn/issues/23582",
                Constraint = "Avoid calling " + nameof(Compilation.AddSyntaxTrees) + " in a loop due to allocation overhead.")]
            private async Task<Compilation> BuildDeclarationCompilationFromScratchAsync(
                SolutionServices solutionServices, CancellationToken cancellationToken)
            {
                try
                {
                    var compilation = CreateEmptyCompilation();

                    var trees = ArrayBuilder<SyntaxTree>.GetInstance(ProjectState.DocumentIds.Count);
                    foreach (var document in ProjectState.OrderedDocumentStates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Include the tree even if the content of the document failed to load.
                        trees.Add(await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false));
                    }

                    compilation = compilation.AddSyntaxTrees(trees);
                    trees.Free();

                    WriteState(new FullDeclarationState(compilation, ImmutableArray<SourceGeneratedDocumentState>.Empty, generatedDocumentsAreFinal: false), solutionServices);
                    return compilation;
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            private Compilation CreateEmptyCompilation()
            {
                var compilationFactory = this.ProjectState.LanguageServices.GetRequiredService<ICompilationFactoryService>();

                if (this.ProjectState.IsSubmission)
                {
                    return compilationFactory.CreateSubmissionCompilation(
                        this.ProjectState.AssemblyName,
                        this.ProjectState.CompilationOptions!,
                        this.ProjectState.HostObjectType);
                }
                else
                {
                    return compilationFactory.CreateCompilation(
                        this.ProjectState.AssemblyName,
                        this.ProjectState.CompilationOptions!);
                }
            }

            private async Task<CompilationInfo> BuildFinalStateFromInProgressStateAsync(
                SolutionState solution, InProgressState state, Compilation inProgressCompilation, CancellationToken cancellationToken)
            {
                try
                {
                    var compilation = await BuildDeclarationCompilationFromInProgressAsync(solution.Services, state, inProgressCompilation, cancellationToken).ConfigureAwait(false);
                    return await FinalizeCompilationAsync(solution, compilation, authoritativeGeneratedDocuments: null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            private async Task<Compilation> BuildDeclarationCompilationFromInProgressAsync(
                SolutionServices solutionServices, InProgressState state, Compilation inProgressCompilation, CancellationToken cancellationToken)
            {
                try
                {
                    var intermediateProjects = state.IntermediateProjects;

                    while (intermediateProjects.Length > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var compilationTranslationAction = intermediateProjects[0].action;
                        inProgressCompilation = await compilationTranslationAction.TransformCompilationAsync(inProgressCompilation, cancellationToken).ConfigureAwait(false);
                        intermediateProjects = intermediateProjects.RemoveAt(0);

                        this.WriteState(State.Create(inProgressCompilation, state.GeneratedDocuments, intermediateProjects), solutionServices);
                    }

                    return inProgressCompilation;
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            private struct CompilationInfo
            {
                public Compilation Compilation { get; }
                public bool HasSuccessfullyLoaded { get; }
                public ImmutableArray<SourceGeneratedDocumentState> GeneratedDocuments { get; }

                public CompilationInfo(Compilation compilation, bool hasSuccessfullyLoaded, ImmutableArray<SourceGeneratedDocumentState> generatedDocuments)
                {
                    Compilation = compilation;
                    HasSuccessfullyLoaded = hasSuccessfullyLoaded;
                    GeneratedDocuments = generatedDocuments;
                }
            }

            /// <summary>
            /// Add all appropriate references to the compilation and set it as our final compilation
            /// state.
            /// </summary>
            /// <param name="authoritativeGeneratedDocuments">The generated documents that can be used since they are already
            /// known to be correct for the given state. This would be non-null in cases where we had computed everything and
            /// ran generators, but then the compilation was garbage collected and are re-creating a compilation but we
            /// still had the prior generated result available.</param>
            private async Task<CompilationInfo> FinalizeCompilationAsync(
                SolutionState solution,
                Compilation compilation,
                ImmutableArray<SourceGeneratedDocumentState>? authoritativeGeneratedDocuments,
                CancellationToken cancellationToken)
            {
                try
                {
                    // if HasAllInformation is false, then this project is always not completed.
                    var hasSuccessfullyLoaded = this.ProjectState.HasAllInformation;

                    var newReferences = new List<MetadataReference>();
                    var metadataReferenceToProjectId = new Dictionary<MetadataReference, ProjectId>();
                    newReferences.AddRange(this.ProjectState.MetadataReferences);

                    foreach (var projectReference in this.ProjectState.ProjectReferences)
                    {
                        var referencedProject = solution.GetProjectState(projectReference.ProjectId);

                        // Even though we're creating a final compilation (vs. an in progress compilation),
                        // it's possible that the target project has been removed.
                        if (referencedProject != null)
                        {
                            // If both projects are submissions, we'll count this as a previous submission link
                            // instead of a regular metadata reference
                            if (referencedProject.IsSubmission)
                            {
                                // if the referenced project is a submission project must be a submission as well:
                                Debug.Assert(this.ProjectState.IsSubmission);

                                var previousSubmissionCompilation =
                                    await solution.GetCompilationAsync(projectReference.ProjectId, cancellationToken).ConfigureAwait(false);

                                compilation = compilation.WithScriptCompilationInfo(
                                    compilation.ScriptCompilationInfo!.WithPreviousScriptCompilation(previousSubmissionCompilation!));
                            }
                            else
                            {
                                var metadataReference = await solution.GetMetadataReferenceAsync(
                                    projectReference, this.ProjectState, cancellationToken).ConfigureAwait(false);

                                // A reference can fail to be created if a skeleton assembly could not be constructed.
                                if (metadataReference != null)
                                {
                                    newReferences.Add(metadataReference);
                                    metadataReferenceToProjectId.Add(metadataReference, projectReference.ProjectId);
                                }
                                else
                                {
                                    hasSuccessfullyLoaded = false;
                                }
                            }
                        }
                    }

                    if (!Enumerable.SequenceEqual(compilation.ExternalReferences, newReferences))
                    {
                        compilation = compilation.WithReferences(newReferences);
                    }

                    var generators = this.ProjectState.AnalyzerReferences.SelectMany(a => a.GetGenerators(this.ProjectState.Language)).ToImmutableArray();

                    // We will finalize the compilation by adding full contents here.
                    // TODO: allow finalize compilation to incrementally update a prior version
                    // https://github.com/dotnet/roslyn/issues/46418
                    var compilationWithoutGeneratedFiles = compilation;

                    ImmutableArray<SourceGeneratedDocumentState> generatedDocuments;

                    if (authoritativeGeneratedDocuments != null)
                    {
                        generatedDocuments = authoritativeGeneratedDocuments.Value;
                    }
                    else
                    {
                        var _ = ArrayBuilder<SourceGeneratedDocumentState>.GetInstance(out var generatedDocumentsBuilder);

                        if (generators.Any())
                        {
                            var additionalTexts = this.ProjectState.AdditionalDocumentStates.Values.SelectAsArray(a => (AdditionalText)new AdditionalTextWithState(a));
                            var compilationFactory = this.ProjectState.LanguageServices.GetRequiredService<ICompilationFactoryService>();

                            var generatorDriver = compilationFactory.CreateGeneratorDriver(
                                    this.ProjectState.ParseOptions!,
                                    generators,
                                    this.ProjectState.AnalyzerOptions.AnalyzerConfigOptionsProvider,
                                    additionalTexts);

                            if (generatorDriver != null)
                            {
                                generatorDriver = generatorDriver.RunGenerators(compilationWithoutGeneratedFiles);

                                foreach (var generatorResult in generatorDriver.GetRunResult().Results)
                                {
                                    foreach (var generatedSource in generatorResult.GeneratedSources)
                                    {
                                        generatedDocumentsBuilder.Add(
                                            SourceGeneratedDocumentState.Create(
                                                generatedSource,
                                                CreateStableSourceGeneratedDocumentId(ProjectState.Id, generatorResult.Generator, generatedSource.HintName),
                                                generatorResult.Generator,
                                                this.ProjectState.LanguageServices,
                                                solution.Services));
                                    }
                                }
                            }
                        }

                        generatedDocuments = generatedDocumentsBuilder.ToImmutable();
                    }

                    compilation = compilation.AddSyntaxTrees(generatedDocuments.Select(d => d.SyntaxTree));

                    RecordAssemblySymbols(compilation, metadataReferenceToProjectId);

                    this.WriteState(
                        new FinalState(
                            State.CreateValueSource(compilation, solution.Services),
                            State.CreateValueSource(compilationWithoutGeneratedFiles, solution.Services),
                            compilationWithoutGeneratedFiles,
                            hasSuccessfullyLoaded,
                            generatedDocuments,
                            State.GetUnrootedSymbols(compilation)),
                        solution.Services);

                    return new CompilationInfo(compilation, hasSuccessfullyLoaded, generatedDocuments);
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            private static DocumentId CreateStableSourceGeneratedDocumentId(ProjectId projectId, ISourceGenerator generator, string hintName)
            {
                // We want the DocumentId generated for a generated output to be stable between Compilations; this is so features that track
                // a document by DocumentId can find it after some change has happened that requires generators to run again.
                // To achieve this we'll just do a crytographic hash of the generator name and hint name; the choice of a cryptographic hash
                // as opposed to a more generic string hash is we actually want to ensure we don't have collisions.
                var generatorName = generator.GetType().FullName;
                Contract.ThrowIfNull(generatorName);

                // Combine the strings together; we'll use Encoding.Unicode since that'll match the underlying format; this can be made much
                // faster once we're on .NET Core since we could directly treat the strings as ReadOnlySpan<char>.
                using var _ = ArrayBuilder<byte>.GetInstance(capacity: (generatorName.Length + hintName.Length + 1) * 2, out var hashInput);
                hashInput.AddRange(Encoding.Unicode.GetBytes(generatorName));

                // Add a null to separate the generator name and hint name; since this is effectively a joining of UTF-16 bytes
                // we'll use a UTF-16 null just to make sure there's absolutely no risk of collision.
                hashInput.AddRange(0, 0);
                hashInput.AddRange(Encoding.Unicode.GetBytes(hintName));

                // The particular choice of crypto algorithm here is arbitrary and can be always changed as necessary.
                var hash = System.Security.Cryptography.SHA256.Create().ComputeHash(hashInput.ToArray());
                Array.Resize(ref hash, 16);
                var guid = new Guid(hash);

                return DocumentId.CreateFromSerialized(projectId, guid, hintName);
            }

            private void RecordAssemblySymbols(Compilation compilation, Dictionary<MetadataReference, ProjectId> metadataReferenceToProjectId)
            {
                RecordSourceOfAssemblySymbol(compilation.Assembly, this.ProjectState.Id);

                foreach (var kvp in metadataReferenceToProjectId)
                {
                    var metadataReference = kvp.Key;
                    var projectId = kvp.Value;

                    var symbol = compilation.GetAssemblyOrModuleSymbol(metadataReference);

                    RecordSourceOfAssemblySymbol(symbol, projectId);
                }
            }

            /// <summary>
            /// Get a metadata reference to this compilation info's compilation with respect to
            /// another project. For cross language references produce a skeletal assembly. If the
            /// compilation is not available, it is built. If a skeletal assembly reference is
            /// needed and does not exist, it is also built.
            /// </summary>
            public async Task<MetadataReference> GetMetadataReferenceAsync(
                SolutionState solution,
                ProjectState fromProject,
                ProjectReference projectReference,
                CancellationToken cancellationToken)
            {
                try
                {

                    // if we already have the compilation and its right kind then use it.
                    if (this.ProjectState.LanguageServices == fromProject.LanguageServices
                        && this.TryGetCompilation(out var compilation))
                    {
                        return compilation.ToMetadataReference(projectReference.Aliases, projectReference.EmbedInteropTypes);
                    }

                    // If same language then we can wrap the other project's compilation into a compilation reference
                    if (this.ProjectState.LanguageServices == fromProject.LanguageServices)
                    {
                        // otherwise, base it off the compilation by building it first.
                        compilation = await this.GetCompilationAsync(solution, cancellationToken).ConfigureAwait(false);
                        return compilation.ToMetadataReference(projectReference.Aliases, projectReference.EmbedInteropTypes);
                    }
                    else
                    {
                        // otherwise get a metadata only image reference that is built by emitting the metadata from the referenced project's compilation and re-importing it.
                        return await this.GetMetadataOnlyImageReferenceAsync(solution, projectReference, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            /// <summary>
            /// Attempts to get (without waiting) a metadata reference to a possibly in progress
            /// compilation. Only actual compilation references are returned. Could potentially 
            /// return null if nothing can be provided.
            /// </summary>
            public MetadataReference? GetPartialMetadataReference(ProjectState fromProject, ProjectReference projectReference)
            {
                var state = ReadState();

                // get compilation in any state it happens to be in right now.
                if (state.CompilationWithoutGeneratedDocuments != null &&
                    state.CompilationWithoutGeneratedDocuments.TryGetValue(out var compilationOpt) &&
                    compilationOpt.HasValue &&
                    ProjectState.LanguageServices == fromProject.LanguageServices)
                {
                    // if we have a compilation and its the correct language, use a simple compilation reference
                    return compilationOpt.Value.ToMetadataReference(projectReference.Aliases, projectReference.EmbedInteropTypes);
                }

                return null;
            }

            /// <summary>
            /// Gets a metadata reference to the metadata-only-image corresponding to the compilation.
            /// </summary>
            private async Task<MetadataReference> GetMetadataOnlyImageReferenceAsync(
                SolutionState solution, ProjectReference projectReference, CancellationToken cancellationToken)
            {
                try
                {
                    using (Logger.LogBlock(FunctionId.Workspace_SkeletonAssembly_GetMetadataOnlyImage, cancellationToken))
                    {
                        var version = await this.GetDependentSemanticVersionAsync(solution, cancellationToken).ConfigureAwait(false);

                        // get or build compilation up to declaration state. this compilation will be used to provide live xml doc comment
                        var declarationCompilation = await this.GetOrBuildDeclarationCompilationAsync(solution.Services, cancellationToken: cancellationToken).ConfigureAwait(false);
                        solution.Workspace.LogTestMessage($"Looking for a cached skeleton assembly for {projectReference.ProjectId} before taking the lock...");

                        if (!MetadataOnlyReference.TryGetReference(solution, projectReference, declarationCompilation, version, out var reference))
                        {
                            // using async build lock so we don't get multiple consumers attempting to build metadata-only images for the same compilation.
                            using (await _buildLock.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
                            {
                                solution.Workspace.LogTestMessage($"Build lock taken for {ProjectState.Id}...");

                                // okay, we still don't have one. bring the compilation to final state since we are going to use it to create skeleton assembly
                                var compilationInfo = await this.GetOrBuildCompilationInfoAsync(solution, lockGate: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                                reference = MetadataOnlyReference.GetOrBuildReference(solution, projectReference, compilationInfo.Compilation, version, cancellationToken);
                            }
                        }
                        else
                        {
                            solution.Workspace.LogTestMessage($"Reusing the already cached skeleton assembly for {projectReference.ProjectId}");
                        }

                        return reference;
                    }
                }
                catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            /// <summary>
            /// check whether the compilation contains any declaration symbol from syntax trees with
            /// given name
            /// </summary>
            public bool? ContainsSymbolsWithNameFromDeclarationOnlyCompilation(string name, SymbolFilter filter, CancellationToken cancellationToken)
            {
                // DO NOT expose declaration only compilation to outside since it can be held alive long time, we don't want to create any symbol from the declaration only compilation.
                var state = this.ReadState();
                return state.DeclarationOnlyCompilation == null
                    ? (bool?)null
                    : state.DeclarationOnlyCompilation.ContainsSymbolsWithName(name, filter, cancellationToken);
            }

            /// <summary>
            /// check whether the compilation contains any declaration symbol from syntax trees with given name
            /// </summary>
            public bool? ContainsSymbolsWithNameFromDeclarationOnlyCompilation(Func<string, bool> predicate, SymbolFilter filter, CancellationToken cancellationToken)
            {
                // DO NOT expose declaration only compilation to outside since it can be held alive long time, we don't want to create any symbol from the declaration only compilation.
                var state = this.ReadState();
                return state.DeclarationOnlyCompilation == null
                    ? (bool?)null
                    : state.DeclarationOnlyCompilation.ContainsSymbolsWithName(predicate, filter, cancellationToken);
            }

            /// <summary>
            /// get all syntax trees that contain declaration node with the given name
            /// </summary>
            public IEnumerable<SyntaxTree>? GetSyntaxTreesWithNameFromDeclarationOnlyCompilation(Func<string, bool> predicate, SymbolFilter filter, CancellationToken cancellationToken)
            {
                var state = this.ReadState();
                if (state.DeclarationOnlyCompilation == null)
                {
                    return null;
                }

                // DO NOT expose declaration only compilation to outside since it can be held alive long time, we don't want to create any symbol from the declaration only compilation.

                // use cloned compilation since this will cause symbols to be created.
                var clone = state.DeclarationOnlyCompilation.Clone();
                return clone.GetSymbolsWithName(predicate, filter, cancellationToken).SelectMany(s => s.DeclaringSyntaxReferences.Select(r => r.SyntaxTree));
            }

            public Task<bool> HasSuccessfullyLoadedAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                var state = this.ReadState();

                if (state.HasSuccessfullyLoaded.HasValue)
                {
                    return state.HasSuccessfullyLoaded.Value ? SpecializedTasks.True : SpecializedTasks.False;
                }
                else
                {
                    return HasSuccessfullyLoadedSlowAsync(solution, cancellationToken);
                }
            }

            private async Task<bool> HasSuccessfullyLoadedSlowAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                var compilationInfo = await GetOrBuildCompilationInfoAsync(solution, lockGate: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                return compilationInfo.HasSuccessfullyLoaded;
            }

            public async Task<ImmutableArray<SourceGeneratedDocumentState>> GetSourceGeneratedDocumentStatesAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                var compilationInfo = await GetOrBuildCompilationInfoAsync(solution, lockGate: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                return compilationInfo.GeneratedDocuments;
            }

            public SourceGeneratedDocumentState? TryGetSourceGeneratedDocumentStateForAlreadyGeneratedId(DocumentId documentId)
            {
                var state = ReadState();

                // If we are in FinalState, then we have correctly ran generators and then know the final contents of the
                // Compilation. The GeneratedDocuments can be filled for intermediate states, but those aren't guaranteed to be
                // correct and can be re-ran later.
                if (state is FinalState finalState)
                {
                    return finalState.GeneratedDocuments.SingleOrDefault(d => d.Id == documentId);
                }

                return null;
            }

            #region Versions

            // Dependent Versions are stored on compilation tracker so they are more likely to survive when unrelated solution branching occurs.

            private AsyncLazy<VersionStamp>? _lazyDependentVersion;
            private AsyncLazy<VersionStamp>? _lazyDependentSemanticVersion;

            public Task<VersionStamp> GetDependentVersionAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                if (_lazyDependentVersion == null)
                {
                    var tmp = solution; // temp. local to avoid a closure allocation for the fast path
                    // note: solution is captured here, but it will go away once GetValueAsync executes.
                    Interlocked.CompareExchange(ref _lazyDependentVersion, new AsyncLazy<VersionStamp>(c => ComputeDependentVersionAsync(tmp, c), cacheResult: true), null);
                }

                return _lazyDependentVersion.GetValueAsync(cancellationToken);
            }

            private async Task<VersionStamp> ComputeDependentVersionAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                var projectState = this.ProjectState;
                var projVersion = projectState.Version;
                var docVersion = await projectState.GetLatestDocumentVersionAsync(cancellationToken).ConfigureAwait(false);

                var version = docVersion.GetNewerVersion(projVersion);
                foreach (var dependentProjectReference in projectState.ProjectReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (solution.ContainsProject(dependentProjectReference.ProjectId))
                    {
                        var dependentProjectVersion = await solution.GetDependentVersionAsync(dependentProjectReference.ProjectId, cancellationToken).ConfigureAwait(false);
                        version = dependentProjectVersion.GetNewerVersion(version);
                    }
                }

                return version;
            }

            public Task<VersionStamp> GetDependentSemanticVersionAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                if (_lazyDependentSemanticVersion == null)
                {
                    var tmp = solution; // temp. local to avoid a closure allocation for the fast path
                    // note: solution is captured here, but it will go away once GetValueAsync executes.
                    Interlocked.CompareExchange(ref _lazyDependentSemanticVersion, new AsyncLazy<VersionStamp>(c => ComputeDependentSemanticVersionAsync(tmp, c), cacheResult: true), null);
                }

                return _lazyDependentSemanticVersion.GetValueAsync(cancellationToken);
            }

            private async Task<VersionStamp> ComputeDependentSemanticVersionAsync(SolutionState solution, CancellationToken cancellationToken)
            {
                var projectState = this.ProjectState;
                var version = await projectState.GetSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

                foreach (var dependentProjectReference in projectState.ProjectReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (solution.ContainsProject(dependentProjectReference.ProjectId))
                    {
                        var dependentProjectVersion = await solution.GetDependentSemanticVersionAsync(dependentProjectReference.ProjectId, cancellationToken).ConfigureAwait(false);
                        version = dependentProjectVersion.GetNewerVersion(version);
                    }
                }

                return version;
            }
            #endregion
        }
    }
}
