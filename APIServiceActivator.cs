using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web;
using System.Web.Routing;

namespace WUI.Common.API
{
    public static class APIServiceActivator
    {
        /// <summary>
        /// Create service and call service action,
        /// set action parameters
        /// </summary>
        /// <param name="context">HttpContext</param>
        /// <param name="descriptor">Service descriptor</param>
        /// <returns>Action value</returns>
        public static object CreateAndCall( HttpContext context, APIServiceDescriptor descriptor )
        {
            if( descriptor.DescriptorType == null )
                throw new APIServiceException(
                    APIServiceConstException.DescriptorTypeNotSet.Format( descriptor.Name ) );

            var service = Activator.CreateInstance( descriptor.DescriptorType );
            CreateAPIServiceContext( service, context, descriptor );

            #region Check service authorization

            var serviceAuthAttr = service.GetType( ).GetCustomAttribute<APIServiceAttrAuthorization>( );
            var serviceAuthorized = false;

            if( serviceAuthAttr != null && ( serviceAuthorized = serviceAuthAttr.Check( context ) ) == false )
            {
                throw new APIServiceException(
                    APIServiceConstException.ServiceUnauthorized.Format( descriptor.Name ) );
            }

            #endregion

            (MethodInfo methodInfo, string mimeTypeParam) action = default;
            var requestParameters = new List<KeyValuePair<string, string>>( );

            descriptor.RequestParameters = requestParameters;

            #region Find action

            //find all public methods, that contain route attribute and has route data
            var actions = service.GetType( ).GetMethods( )
                .Select( m =>
                {
                    RouteData routeData = null;
                    var routeAttr = m.GetCustomAttribute<APIServiceRouteAttribute>( );

                    var routePath = routeAttr?.Route != null ? routeAttr.Route : m.Name.ToLower( );
                    var requestType = string.Empty;
                    var route = new Route( $"{descriptor.Route}{routeAttr?.Prefix}{routePath}", null );

                    if( routeAttr?.Constraints != null )
                    {
                        //apply constraints
                        route.Constraints = new RouteValueDictionary( );

                        for( int i = 0; i < routeAttr.Constraints.Length; i += 2 )
                        {
                            var key = i < routeAttr.Constraints.Length
                                ? routeAttr.Constraints[i]
                                : string.Empty;
                            var value = i + 1 < routeAttr.Constraints.Length
                                ? routeAttr.Constraints[i + 1]
                                : string.Empty;

                            if( string.IsNullOrEmpty( key ) == false &&
                                string.IsNullOrEmpty( value ) == false )
                            {
                                route.Constraints.Add( key, value );
                            }
                        }

                    }

                    routeData = route.GetRouteData( context.Request.RequestContext.HttpContext );

                    //find request type attribute
                    var requestTypeAttr = m.GetCustomAttribute<APIServiceRequestTypeAttribute>( );
                    if( requestTypeAttr != null && string.IsNullOrEmpty( requestTypeAttr.Type ) == false )
                    {
                        requestType = requestTypeAttr.Type.ToUpper( );
                    }

                    return new
                    {
                        methodInfo = m,
                        routeData = routeData,
                        routePath = routePath,
                        requestType = requestType,
                        mimeTypeParam = routeAttr?.ResponseMimeTypeParam,
                        equalQuery = routeAttr?.QueryParamEqual ?? APIServiceQueryParamEqual.Match
                    };
                } ).Where( v =>
                {
                    if( string.IsNullOrEmpty( v.requestType ) == false )
                    {
                        return v.requestType == descriptor.RequestType &&
                               v.routeData != null;
                    }
                    else
                    {
                        return v.routeData != null;
                    }

                } ).ToList( );

            if( actions.Count > 1 )
            {
                //find action by input parameters
                //compare number

                var com = actions.Select( v =>
                {
                    var pcount = v.methodInfo.GetParameters( ).Length;
                    var vcount = v.routeData.Values.Count +
                                 ( descriptor.Parameters != null
                                     ? descriptor.Parameters.Count( )
                                     : 0 );
                    return new
                    {
                        methodInfo = v.methodInfo,
                        routeValues = v.routeData.Values,
                        //TODO: need match by parameters name from query and parameters in method (count, name)
                        isMatch = (pcount == vcount) || v.equalQuery == APIServiceQueryParamEqual.Any,
                        routePath = v.routePath,
                        mimeTypeParam = v.mimeTypeParam
                    };
                } ).Where( c => c.isMatch ).ToList( );

                if( com.Count == 1 )
                {
                    action = (com[0].methodInfo, com[0].mimeTypeParam);

                    requestParameters.AddRange( com[0].routeValues.Select(
                        v => new KeyValuePair<string, string>( v.Key,
                                v.Value.ToString( ) ) ) );
                }
                else if( com.Count > 1 )
                {
                    throw new APIServiceException( APIServiceConstException
                        .ActionAlreadyDeclared.Format( descriptor.Name, com[0].routePath ) );
                }
            }
            else if( actions.Count == 1 )
            {
                action = (actions[0].methodInfo, actions[0].mimeTypeParam);

                //route parameters has priority an order on request parameters
                requestParameters.AddRange( actions[0].routeData.Values.Select(
                    v => new KeyValuePair<string, string>( v.Key, v.Value.ToString( ) ) ) );
            }

            #endregion

            if( action.methodInfo == null )
                throw new APIServiceException( APIServiceConstException.ServiceActionNotFound.Format( descriptor.Name ) );

            #region Check action authorization

            if( serviceAuthorized == false )
            {
                var actionAuthAttr = action.methodInfo.GetCustomAttribute<APIServiceAttrAuthorization>( );
                if( actionAuthAttr != null && actionAuthAttr.Check( context ) == false )
                {
                    throw new APIServiceException(
                        APIServiceConstException.ServiceUnauthorized.Format( descriptor.Name ) );
                }
            }

            #endregion

            if( descriptor.Parameters != null )
            {
                requestParameters.AddRange( descriptor.Parameters );
            }

            //set parameters with an order
            var actionParameters = action.methodInfo.GetParameters( )
                .Select( p => requestParameters.FirstOrDefault( v => v.Key == p.Name ).Value )
                .Where( v => string.IsNullOrEmpty( v ) == false ).ToArray<object>( );

            object result = null;

            #region Set response mime-type from query

            if( string.IsNullOrEmpty( action.mimeTypeParam ) == false )
            {
                var mimeQueryParam = requestParameters.FirstOrDefault( v => v.Key == action.mimeTypeParam );

                if( string.IsNullOrEmpty( mimeQueryParam.Value ) )
                {
                    throw new APIServiceException( APIServiceConstException.QueryParamNotFound.Format( action.mimeTypeParam ) );
                }
                else
                {
                    descriptor.ResponseMimeType = mimeQueryParam.Value;
                    requestParameters.Remove( mimeQueryParam );
                }
            }

            #endregion

            try
            {
                //call action with parameters
                result = action.methodInfo.Invoke( service, actionParameters );
            }
            catch( TargetParameterCountException )
            {
                throw new APIServiceException(
                    APIServiceConstException.ActionParameterCount.Format( descriptor.Name,
                        action.methodInfo.Name ) );
            }
            catch( Exception ex )
            {
                if( ex.InnerException is APIServiceException )
                {
                    throw ex.InnerException;
                }
                else
                {
                    throw new APIServiceException(
                        APIServiceConstException.ExecuteAction.Format( descriptor.Name, action.methodInfo.Name ) );
                }
            }

            return result;
        }

        private static void CreateAPIServiceContext( object target, HttpContext httpContext, APIServiceDescriptor descriptor )
        {
            if( target is IAPIService apiServiceImp )
            {
                apiServiceImp.Context = new APIServiceContext
                {
                    HttpContext = httpContext,
                    InvokeDescriptor = descriptor
                };
            }
        }
    }
}