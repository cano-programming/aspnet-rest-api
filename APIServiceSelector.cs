using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web;

namespace WUI.Common.API
{
    /// <summary>
    /// Selector will look all classes in passed assembly
    /// and cache them
    /// </summary>
    public static class APIServiceSelector
    {
        private static readonly Dictionary<Tuple<string, string>, Type> CacheTypes =
            new Dictionary<Tuple<string, string>, Type>( );

        private static readonly ReaderWriterLock CacheLock = new ReaderWriterLock();

        private const string Suffix = "APIService";

        /// <summary>
        /// Find service descriptor type by route path
        /// </summary>
        /// <returns></returns>
        public static void SetType( ref APIServiceDescriptor descriptor,
            Assembly assembly )
        {
            if (string.IsNullOrEmpty( descriptor.Name ))
                throw new APIServiceException( APIServiceConstException.NotSetDescriptor );

            var key = new Tuple<string, string>( descriptor.Name, descriptor.Version );

            //find service type in cache
            if (CacheTypes.ContainsKey( key ))
            {
                CacheLock.AcquireReaderLock( Timeout.Infinite );
                try
                {
                    descriptor.DescriptorType = CacheTypes[key];
                }
                catch
                {
                    throw new APIServiceException(
                        APIServiceConstException.ReadTypeDescriptor.Format( descriptor.Name ) );
                }
                finally
                {
                    CacheLock.ReleaseReaderLock( );
                }
            }
            //find service type in assembly and caching it
            else
            {
                CacheLock.AcquireWriterLock( Timeout.Infinite );
                try
                {
                    if (CacheTypes.ContainsKey( key ))
                    {
                        descriptor.DescriptorType = CacheTypes[key];
                    }
                    else
                    {
                        var types = assembly.GetTypes( )
                            .Where(
                                v => v.IsPublic && v.IsClass && v.Name.EndsWith( Suffix ) )
                            .Select( t =>
                            {
                                var name = t.Name.Substring( 0, t.Name.IndexOf( Suffix, StringComparison.Ordinal ) );
                                var version = new[] { "v1" };

                                //find alias attribute
                                var aliasAttr = t.GetCustomAttribute<APIServiceAliasAttribute>( );
                                if (aliasAttr != null)
                                {
                                    name = aliasAttr.Alias;
                                }
                                //find version attribute
                                var verAttr = t.GetCustomAttribute<APIServiceVersionAttribute>( );
                                if (verAttr != null && verAttr.Version != null)
                                {
                                    version = verAttr.Version;
                                }

                                return new
                                {
                                    type = t,
                                    version = version,
                                    name = name.ToLower( )
                                };
                            } ).Where( n => n.name == key.Item1 && n.version.Contains( key.Item2 ) ).ToList( );

                        if (types.Count > 1)
                        {
                            throw new APIServiceException(
                                APIServiceConstException.DescriptorTypeDuplicate.Format(
                                    descriptor.Name, descriptor.Version ) );
                        }
                        else if (types.Count == 1)
                        {
                            CacheTypes[key] = descriptor.DescriptorType = types[0].type;
                        }
                        //service type not found
                        else
                        {
                            throw new APIServiceException(
                                APIServiceConstException.DescriptorTypeNotFound.Format(
                                    descriptor.Name, descriptor.Version ) );
                        }
                    }
                }
                catch (APIServiceException)
                {
                    throw;
                }
                catch
                {
                    throw new APIServiceException(
                        APIServiceConstException.WriteTypeDescriptor.Format( descriptor.Name ) );
                }
                finally
                {
                    CacheLock.ReleaseWriterLock( );
                }
            }
        }
    }
}