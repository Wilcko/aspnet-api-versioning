﻿namespace Microsoft.Web.Http.Controllers
{
    using Routing;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Text;
    using System.Web.Http;
    using System.Web.Http.Controllers;
    using System.Web.Http.Routing;
    using System.Web.Http.Services;
    using static System.Net.HttpStatusCode;
    using static System.StringComparer;

    /// <content>
    /// Provides additional content for the <see cref="ApiVersionActionSelector"/> class.
    /// </content>
    public partial class ApiVersionActionSelector
    {
        /// <summary>
        /// <para>All caching is in a dedicated cache class, which may be optionally shared across selector instances.</para>
        /// <para>Make this a private nested class so that nobody else can conflict with our state.</para>
        /// <para>Cache is initialized during ctor on a single thread.</para>
        /// </summary>
        private sealed class ActionSelectorCacheItem
        {
            private static readonly HttpMethod[] cacheListVerbKinds = new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Post };
            private static readonly Type ApiControllerType = typeof( ApiController );
            private readonly HttpControllerDescriptor controllerDescriptor;
            private readonly CandidateAction[] combinedCandidateActions;
            private readonly IDictionary<HttpActionDescriptor, string[]> actionParameterNames = new Dictionary<HttpActionDescriptor, string[]>();
            private readonly ILookup<string, HttpActionDescriptor> combinedActionNameMapping;
            private StandardActionSelectionCache standardActions;

            internal ActionSelectorCacheItem( HttpControllerDescriptor controllerDescriptor )
            {
                Contract.Requires( controllerDescriptor != null );

                this.controllerDescriptor = Decorator.GetInner( controllerDescriptor );

                var allMethods = this.controllerDescriptor.ControllerType.GetMethods( BindingFlags.Instance | BindingFlags.Public );
                var validMethods = Array.FindAll( allMethods, IsValidActionMethod );

                combinedCandidateActions = new CandidateAction[validMethods.Length];

                for ( var i = 0; i < validMethods.Length; i++ )
                {
                    var method = validMethods[i];
                    var actionDescriptor = new ReflectedHttpActionDescriptor( controllerDescriptor, method );
                    var actionBinding = actionDescriptor.ActionBinding;

                    combinedCandidateActions[i] = new CandidateAction( actionDescriptor );

                    actionParameterNames.Add(
                        actionDescriptor,
                        actionBinding.ParameterBindings
                            .Where( binding => !binding.Descriptor.IsOptional && binding.Descriptor.ParameterType.CanConvertFromString() && binding.WillReadUri() )
                            .Select( binding => binding.Descriptor.Prefix ?? binding.Descriptor.ParameterName ).ToArray() );
                }

                combinedActionNameMapping =
                    combinedCandidateActions
                    .Select( c => c.ActionDescriptor )
                    .ToLookup( actionDesc => actionDesc.ActionName, OrdinalIgnoreCase );
            }

            internal HttpControllerDescriptor HttpControllerDescriptor => controllerDescriptor;

            private void InitializeStandardActions()
            {
                if ( standardActions != null )
                {
                    return;
                }

                var selectionCache = new StandardActionSelectionCache();

                if ( controllerDescriptor.IsAttributeRouted() )
                {
                    selectionCache.StandardCandidateActions = new CandidateAction[0];
                }
                else
                {
                    var standardCandidateActions = new List<CandidateAction>();

                    for ( var i = 0; i < combinedCandidateActions.Length; i++ )
                    {
                        var candidate = combinedCandidateActions[i];
                        var action = (ReflectedHttpActionDescriptor) candidate.ActionDescriptor;

                        if ( action.MethodInfo.DeclaringType != controllerDescriptor.ControllerType || !candidate.ActionDescriptor.IsAttributeRouted() )
                        {
                            standardCandidateActions.Add( candidate );
                        }
                    }

                    selectionCache.StandardCandidateActions = standardCandidateActions.ToArray();
                }

                selectionCache.StandardActionNameMapping = selectionCache.StandardCandidateActions.Select( c => c.ActionDescriptor ).ToLookup( actionDesc => actionDesc.ActionName, OrdinalIgnoreCase );

                var len = cacheListVerbKinds.Length;

                selectionCache.CacheListVerbs = new CandidateAction[len][];

                for ( var i = 0; i < len; i++ )
                {
                    selectionCache.CacheListVerbs[i] = FindActionsForVerbWorker( cacheListVerbKinds[i], selectionCache.StandardCandidateActions );
                }

                standardActions = selectionCache;
            }

            internal HttpActionDescriptor SelectAction( HttpControllerContext controllerContext, Func<HttpControllerContext, IReadOnlyList<HttpActionDescriptor>, HttpActionDescriptor> selector )
            {
                Contract.Requires( controllerContext != null );
                Contract.Requires( selector != null );
                Contract.Ensures( Contract.Result<HttpActionDescriptor>() != null );

                InitializeStandardActions();

                var selectedCandidates = FindMatchingActions( controllerContext );

                if ( selectedCandidates.Count == 0 )
                {
                    throw new HttpResponseException( CreateSelectionError( controllerContext ) );
                }

                var action = selector( controllerContext, selectedCandidates ) as CandidateHttpActionDescriptor;

                if ( action != null )
                {
                    ElevateRouteData( controllerContext, action.CandidateAction );
                    return action;
                }

                if ( selectedCandidates.Count == 1 )
                {
                    throw new HttpResponseException( CreateSelectionError( controllerContext ) );
                }

                var ambiguityList = CreateAmbiguousMatchList( selectedCandidates );
                throw new InvalidOperationException( SR.ApiControllerActionSelector_AmbiguousMatch.FormatDefault( ambiguityList ) );
            }

            private static void ElevateRouteData( HttpControllerContext controllerContext, CandidateActionWithParams selectedCandidate ) => controllerContext.RouteData = selectedCandidate.RouteDataSource;

            private IReadOnlyList<CandidateHttpActionDescriptor> FindMatchingActions( HttpControllerContext controllerContext, bool ignoreVerbs = false )
            {
                Contract.Requires( controllerContext != null );
                Contract.Ensures( Contract.Result<IReadOnlyList<CandidateHttpActionDescriptor>>() != null );

                var routeData = controllerContext.RouteData;
                var subRoutes = routeData.GetSubRoutes();
                var actionsWithParameters = GetInitialCandidateWithParameterListForRegularRoutes( controllerContext, ignoreVerbs )
                                    .Union( GetInitialCandidateWithParameterListForDirectRoutes( controllerContext, subRoutes, ignoreVerbs ) );
                var actionsFoundByParams = FindActionMatchRequiredRouteAndQueryParameters( actionsWithParameters );
                var orderCandidates = RunOrderFilter( actionsFoundByParams );
                var precedenceCandidates = RunPrecedenceFilter( orderCandidates );
                var selectedCandidates = FindActionMatchMostRouteAndQueryParameters( precedenceCandidates );

                return selectedCandidates.Select( c => new CandidateHttpActionDescriptor( c ) ).ToArray();
            }

            [SuppressMessage( "Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Caller is responsible for disposing of response instance." )]
            private HttpResponseMessage CreateSelectionError( HttpControllerContext controllerContext )
            {
                Contract.Ensures( Contract.Result<HttpResponseMessage>() != null );

                var actionsFoundByParams = FindMatchingActions( controllerContext, ignoreVerbs: true );

                if ( actionsFoundByParams.Count > 0 )
                {
                    return Create405Response( controllerContext, actionsFoundByParams );
                }

                return CreateActionNotFoundResponse( controllerContext );
            }

            [SuppressMessage( "Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Caller is responsible for disposing of response instance." )]
            private static HttpResponseMessage Create405Response( HttpControllerContext controllerContext, IEnumerable<HttpActionDescriptor> allowedCandidates )
            {
                Contract.Requires( controllerContext != null );
                Contract.Requires( allowedCandidates != null );
                Contract.Ensures( Contract.Result<HttpResponseMessage>() != null );

                var incomingMethod = controllerContext.Request.Method;
                var response = controllerContext.Request.CreateErrorResponse( MethodNotAllowed, SR.ApiControllerActionSelector_HttpMethodNotSupported.FormatDefault( incomingMethod ) );
                var methods = new HashSet<HttpMethod>();

                foreach ( var candidate in allowedCandidates )
                {
                    methods.UnionWith( candidate.SupportedHttpMethods );
                }

                foreach ( var method in methods )
                {
                    response.Content.Headers.Allow.Add( method.ToString() );
                }

                return response;
            }

            [SuppressMessage( "Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Handled by the caller." )]
            private HttpResponseMessage CreateActionNotFoundResponse( HttpControllerContext controllerContext )
            {
                Contract.Requires( controllerContext != null );
                Contract.Ensures( Contract.Result<HttpResponseMessage>() != null );

                var message = SR.ResourceNotFound.FormatDefault( controllerContext.Request.RequestUri );
                var messageDetail = SR.ApiControllerActionSelector_ActionNotFound.FormatDefault( controllerDescriptor.ControllerName );
                return controllerContext.Request.CreateErrorResponse( NotFound, message, messageDetail );
            }

            [SuppressMessage( "Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Handled by the caller." )]
            private HttpResponseMessage CreateActionNotFoundResponse( HttpControllerContext controllerContext, string actionName )
            {
                Contract.Requires( controllerContext != null );
                Contract.Ensures( Contract.Result<HttpResponseMessage>() != null );

                var message = SR.ResourceNotFound.FormatDefault( controllerContext.Request.RequestUri );
                var messageDetail = SR.ApiControllerActionSelector_ActionNameNotFound.FormatDefault( controllerDescriptor.ControllerName, actionName );
                return controllerContext.Request.CreateErrorResponse( NotFound, message, messageDetail );
            }

            private static List<CandidateActionWithParams> GetInitialCandidateWithParameterListForDirectRoutes( HttpControllerContext controllerContext, IEnumerable<IHttpRouteData> subRoutes, bool ignoreVerbs )
            {
                Contract.Requires( controllerContext != null );
                Contract.Ensures( Contract.Result<List<CandidateActionWithParams>>() != null );

                var candidateActionWithParams = new List<CandidateActionWithParams>();

                if ( subRoutes == null )
                {
                    return candidateActionWithParams;
                }

                var request = controllerContext.Request;
                var incomingMethod = controllerContext.Request.Method;
                var queryNameValuePairs = request.GetQueryNameValuePairs();

                foreach ( var subRouteData in subRoutes )
                {
                    var combinedParameterNames = GetCombinedParameterNames( queryNameValuePairs, subRouteData.Values );
                    var candidates = subRouteData.Route.GetDirectRouteCandidates();
                    var actionName = default( string );

                    subRouteData.Values.TryGetValue( RouteValueKeys.Action, out actionName );

                    foreach ( var candidate in candidates )
                    {
                        if ( actionName == null || candidate.MatchName( actionName ) )
                        {
                            if ( ignoreVerbs || candidate.MatchVerb( incomingMethod ) )
                            {
                                candidateActionWithParams.Add( new CandidateActionWithParams( candidate, combinedParameterNames, subRouteData ) );
                            }
                        }
                    }
                }

                return candidateActionWithParams;
            }

            private IEnumerable<CandidateActionWithParams> GetInitialCandidateWithParameterListForRegularRoutes( HttpControllerContext controllerContext, bool ignoreVerbs = false )
            {
                Contract.Requires( controllerContext != null );
                Contract.Ensures( Contract.Result<IEnumerable<CandidateActionWithParams>>() != null );

                var candidates = GetInitialCandidateList( controllerContext, ignoreVerbs );
                return GetCandidateActionsWithBindings( controllerContext, candidates );
            }

            private CandidateAction[] GetInitialCandidateList( HttpControllerContext controllerContext, bool ignoreVerbs = false )
            {
                Contract.Requires( controllerContext != null );
                Contract.Ensures( Contract.Result<CandidateAction[]>() != null );

                var actionName = default( string );
                var incomingMethod = controllerContext.Request.Method;
                var routeData = controllerContext.RouteData;
                var candidates = default( CandidateAction[] );

                if ( routeData.Values.TryGetValue( RouteValueKeys.Action, out actionName ) )
                {
                    var actionsFoundByName = standardActions.StandardActionNameMapping[actionName].ToArray();

                    if ( actionsFoundByName.Length == 0 )
                    {
                        throw new HttpResponseException( CreateActionNotFoundResponse( controllerContext, actionName ) );
                    }

                    var candidatesFoundByName = new CandidateAction[actionsFoundByName.Length];

                    for ( var i = 0; i < actionsFoundByName.Length; i++ )
                    {
                        candidatesFoundByName[i] = new CandidateAction( actionsFoundByName[i] );
                    }

                    if ( ignoreVerbs )
                    {
                        candidates = candidatesFoundByName;
                    }
                    else
                    {
                        candidates = FilterIncompatibleVerbs( incomingMethod, candidatesFoundByName );
                    }
                }
                else
                {
                    if ( ignoreVerbs )
                    {
                        candidates = standardActions.StandardCandidateActions;
                    }
                    else
                    {
                        candidates = FindActionsForVerb( incomingMethod, standardActions.CacheListVerbs, standardActions.StandardCandidateActions );
                    }
                }

                return candidates;
            }

            private static CandidateAction[] FilterIncompatibleVerbs( HttpMethod incomingMethod, CandidateAction[] candidatesFoundByName ) =>
                candidatesFoundByName.Where( c => c.ActionDescriptor.SupportedHttpMethods.Contains( incomingMethod ) ).ToArray();

            internal ILookup<string, HttpActionDescriptor> GetActionMapping() => combinedActionNameMapping;

            private static ISet<string> GetCombinedParameterNames( IEnumerable<KeyValuePair<string, string>> queryNameValuePairs, IDictionary<string, object> routeValues )
            {
                Contract.Requires( routeValues != null );
                Contract.Ensures( Contract.Result<ISet<string>>() != null );

                var routeParameterNames = new HashSet<string>( routeValues.Keys, OrdinalIgnoreCase );

                routeParameterNames.Remove( RouteValueKeys.Controller );
                routeParameterNames.Remove( RouteValueKeys.Action );

                var combinedParameterNames = new HashSet<string>( routeParameterNames, OrdinalIgnoreCase );

                if ( queryNameValuePairs != null )
                {
                    foreach ( var queryNameValuePair in queryNameValuePairs )
                    {
                        combinedParameterNames.Add( queryNameValuePair.Key );
                    }
                }

                return combinedParameterNames;
            }

            private List<CandidateActionWithParams> FindActionMatchRequiredRouteAndQueryParameters( IEnumerable<CandidateActionWithParams> candidatesFound )
            {
                Contract.Requires( candidatesFound != null );
                Contract.Ensures( Contract.Result<List<CandidateActionWithParams>>() != null );

                var matches = new List<CandidateActionWithParams>();

                foreach ( var candidate in candidatesFound )
                {
                    var descriptor = candidate.ActionDescriptor;

                    if ( descriptor.ControllerDescriptor == controllerDescriptor && IsSubset( actionParameterNames[descriptor], candidate.CombinedParameterNames ) )
                    {
                        matches.Add( candidate );
                    }
                }

                return matches;
            }

            private List<CandidateActionWithParams> FindActionMatchMostRouteAndQueryParameters( List<CandidateActionWithParams> candidatesFound ) =>
                candidatesFound.Count < 2 ? candidatesFound : candidatesFound.GroupBy( c => actionParameterNames[c.ActionDescriptor].Length ).OrderByDescending( g => g.Key ).First().ToList();

            private static CandidateActionWithParams[] GetCandidateActionsWithBindings( HttpControllerContext controllerContext, CandidateAction[] candidatesFound )
            {
                Contract.Requires( controllerContext != null );
                Contract.Requires( candidatesFound != null );
                Contract.Ensures( Contract.Result<CandidateActionWithParams[]>() != null );

                var request = controllerContext.Request;
                var queryNameValuePairs = request.GetQueryNameValuePairs();
                var routeData = controllerContext.RouteData;
                var routeValues = routeData.Values;
                var combinedParameterNames = GetCombinedParameterNames( queryNameValuePairs, routeValues );
                var candidatesWithParams = Array.ConvertAll( candidatesFound, candidate => new CandidateActionWithParams( candidate, combinedParameterNames, routeData ) );

                return candidatesWithParams;
            }

            private static bool IsSubset( string[] actionParameters, ISet<string> routeAndQueryParameters )
            {
                Contract.Requires( actionParameters != null );
                Contract.Requires( routeAndQueryParameters != null );

                foreach ( var actionParameter in actionParameters )
                {
                    if ( !routeAndQueryParameters.Contains( actionParameter ) )
                    {
                        return false;
                    }
                }

                return true;
            }

            private static List<CandidateActionWithParams> RunOrderFilter( List<CandidateActionWithParams> candidatesFound )
            {
                Contract.Requires( candidatesFound != null );
                Contract.Ensures( Contract.Result<List<CandidateActionWithParams>>() != null );

                if ( candidatesFound.Count == 0 )
                {
                    return candidatesFound;
                }

                var minOrder = candidatesFound.Min( c => c.CandidateAction.Order );

                return candidatesFound.Where( c => c.CandidateAction.Order == minOrder ).AsList();
            }

            private static List<CandidateActionWithParams> RunPrecedenceFilter( List<CandidateActionWithParams> candidatesFound )
            {
                Contract.Requires( candidatesFound != null );
                Contract.Ensures( Contract.Result<List<CandidateActionWithParams>>() != null );

                if ( candidatesFound.Count == 0 )
                {
                    return candidatesFound;
                }

                var highestPrecedence = candidatesFound.Min( c => c.CandidateAction.Precedence );

                return candidatesFound.Where( c => c.CandidateAction.Precedence == highestPrecedence ).AsList();
            }

            private static CandidateAction[] FindActionsForVerb( HttpMethod verb, CandidateAction[][] actionsByVerb, CandidateAction[] otherActions )
            {
                Contract.Requires( verb != null );
                Contract.Requires( actionsByVerb != null );
                Contract.Requires( otherActions != null );
                Contract.Ensures( Contract.Result<CandidateAction[]>() != null );

                for ( var i = 0; i < cacheListVerbKinds.Length; i++ )
                {
                    if ( ReferenceEquals( verb, cacheListVerbKinds[i] ) )
                    {
                        return actionsByVerb[i];
                    }
                }

                return FindActionsForVerbWorker( verb, otherActions );
            }

            private static CandidateAction[] FindActionsForVerbWorker( HttpMethod verb, CandidateAction[] candidates )
            {
                Contract.Requires( verb != null );
                Contract.Requires( candidates != null );
                Contract.Ensures( Contract.Result<CandidateAction[]>() != null );

                var listCandidates = new List<CandidateAction>();
                FindActionsForVerbWorker( verb, candidates, listCandidates );
                return listCandidates.ToArray();
            }

            private static void FindActionsForVerbWorker( HttpMethod verb, CandidateAction[] candidates, List<CandidateAction> listCandidates )
            {
                Contract.Requires( verb != null );
                Contract.Requires( candidates != null );
                Contract.Requires( listCandidates != null );

                foreach ( var candidate in candidates )
                {
                    var action = candidate.ActionDescriptor;

                    if ( action != null && action.SupportedHttpMethods.Contains( verb ) )
                    {
                        listCandidates.Add( candidate );
                    }
                }
            }

            private static string CreateAmbiguousMatchList( IEnumerable<CandidateHttpActionDescriptor> ambiguousCandidates )
            {
                Contract.Requires( ambiguousCandidates != null );
                Contract.Ensures( Contract.Result<string>() != null );

                var exceptionMessageBuilder = new StringBuilder();

                foreach ( var descriptor in ambiguousCandidates )
                {
                    var controllerDescriptor = descriptor.ControllerDescriptor;
                    var controllerTypeName = default( string );

                    if ( controllerDescriptor != null && controllerDescriptor.ControllerType != null )
                    {
                        controllerTypeName = controllerDescriptor.ControllerType.FullName;
                    }
                    else
                    {
                        controllerTypeName = string.Empty;
                    }

                    exceptionMessageBuilder.AppendLine();
                    exceptionMessageBuilder.Append( SR.ActionSelector_AmbiguousMatchType.FormatDefault( descriptor.ActionName, controllerTypeName ) );
                }

                return exceptionMessageBuilder.ToString();
            }

            private static bool IsValidActionMethod( MethodInfo methodInfo )
            {
                Contract.Requires( methodInfo != null );

                if ( methodInfo.IsSpecialName )
                {
                    return false;
                }

                if ( methodInfo.GetBaseDefinition().DeclaringType.IsAssignableFrom( ApiControllerType ) )
                {
                    return false;
                }

                if ( methodInfo.GetCustomAttribute<NonActionAttribute>() != null )
                {
                    return false;
                }

                return true;
            }
        }
    }
}
