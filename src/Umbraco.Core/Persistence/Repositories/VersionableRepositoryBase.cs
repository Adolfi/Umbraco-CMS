﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NPoco;
using Umbraco.Core.Configuration;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Editors;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Models.Rdbms;

using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.Services;
using Umbraco.Core.Dynamics;
using Umbraco.Core.IO;
using Umbraco.Core.Persistence.Mappers;

namespace Umbraco.Core.Persistence.Repositories
{
    internal abstract class VersionableRepositoryBase<TId, TEntity> : NPocoRepositoryBase<TId, TEntity>
        where TEntity : class, IAggregateRoot
    {
        private readonly IContentSection _contentSection;

        protected VersionableRepositoryBase(IDatabaseUnitOfWork work, CacheHelper cache, ILogger logger, ISqlSyntaxProvider sqlSyntax, IContentSection contentSection, IMappingResolver mappingResolver)
            : base(work, cache, logger, sqlSyntax, mappingResolver)
        {
            _contentSection = contentSection;
        }

        #region IRepositoryVersionable Implementation

        public virtual IEnumerable<TEntity> GetAllVersions(int id)
        {
            var sql = new Sql();
            sql.Select("*")
                .From<ContentVersionDto>(SqlSyntax)
                .InnerJoin<ContentDto>(SqlSyntax)
                .On<ContentVersionDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.NodeId)
                .InnerJoin<NodeDto>(SqlSyntax)
                .On<ContentDto, NodeDto>(SqlSyntax, left => left.NodeId, right => right.NodeId)
                .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId)
                .Where<NodeDto>(SqlSyntax, x => x.NodeId == id)
                .OrderByDescending<ContentVersionDto>(SqlSyntax, x => x.VersionDate);

            var dtos = Database.Fetch<ContentVersionDto>(sql);
            return dtos.Select(x => GetByVersion(x.VersionId));
        }

        public virtual void DeleteVersion(Guid versionId)
        {
            var dto = Database.FirstOrDefault<ContentVersionDto>("WHERE versionId = @VersionId", new { VersionId = versionId });
            if(dto == null) return;

            //Ensure that the lastest version is not deleted
            var latestVersionDto = Database.FirstOrDefault<ContentVersionDto>("WHERE ContentId = @Id ORDER BY VersionDate DESC", new { Id = dto.NodeId });
            if(latestVersionDto.VersionId == dto.VersionId)
                return;

            using (var transaction = Database.GetTransaction())
            {
                PerformDeleteVersion(dto.NodeId, versionId);

                transaction.Complete();
            }
        }

        public virtual void DeleteVersions(int id, DateTime versionDate)
        {
            //Ensure that the latest version is not part of the versions being deleted
            var latestVersionDto = Database.FirstOrDefault<ContentVersionDto>("WHERE ContentId = @Id ORDER BY VersionDate DESC", new { Id = id });
            var list =
                Database.Fetch<ContentVersionDto>(
                    "WHERE versionId <> @VersionId AND (ContentId = @Id AND VersionDate < @VersionDate)",
                    new {VersionId = latestVersionDto.VersionId, Id = id, VersionDate = versionDate});
            if (list.Any() == false) return;

            using (var transaction = Database.GetTransaction())
            {
                foreach (var dto in list)
                {
                    PerformDeleteVersion(id, dto.VersionId);
                }

                transaction.Complete();
            }
        }

        public abstract TEntity GetByVersion(Guid versionId);

        /// <summary>
        /// Protected method to execute the delete statements for removing a single version for a TEntity item.
        /// </summary>
        /// <param name="id">Id of the <see cref="TEntity"/> to delete a version from</param>
        /// <param name="versionId">Guid id of the version to delete</param>
        protected abstract void PerformDeleteVersion(int id, Guid versionId);

        #endregion

        public int CountDescendants(int parentId, string contentTypeAlias = null)
        {
            var pathMatch = parentId == -1
                ? "-1,"
                : "," + parentId + ",";
            var sql = new Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>(SqlSyntax)
                    .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.Path.Contains(pathMatch));
            }
            else
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>(SqlSyntax)
                    .InnerJoin<ContentDto>(SqlSyntax)
                    .On<NodeDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>(SqlSyntax)
                    .On<ContentTypeDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.Path.Contains(pathMatch))
                    .Where<ContentTypeDto>(SqlSyntax, x => x.Alias == contentTypeAlias);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        public int CountChildren(int parentId, string contentTypeAlias = null)
        {
            var sql = new Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>(SqlSyntax)
                    .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.ParentId == parentId);
            }
            else
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>(SqlSyntax)
                    .InnerJoin<ContentDto>(SqlSyntax)
                    .On<NodeDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>(SqlSyntax)
                    .On<ContentTypeDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.ParentId == parentId)
                    .Where<ContentTypeDto>(SqlSyntax, x => x.Alias == contentTypeAlias);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        /// <summary>
        /// Get the total count of entities
        /// </summary>
        /// <param name="contentTypeAlias"></param>
        /// <returns></returns>
        public int Count(string contentTypeAlias = null)
        {
            var sql = new Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>(SqlSyntax)
                    .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId);
            }
            else
            {
                sql.Select("COUNT(*)")
                    .From<NodeDto>(SqlSyntax)
                    .InnerJoin<ContentDto>(SqlSyntax)
                    .On<NodeDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>(SqlSyntax)
                    .On<ContentTypeDto, ContentDto>(SqlSyntax, left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(SqlSyntax, x => x.NodeObjectType == NodeObjectTypeId)
                    .Where<ContentTypeDto>(SqlSyntax, x => x.Alias == contentTypeAlias);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        /// <summary>
        /// This removes associated tags from the entity - used generally when an entity is recycled
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="tagRepo"></param>
        protected void ClearEntityTags(IContentBase entity, ITagRepository tagRepo)
        {
            tagRepo.ClearTagsFromEntity(entity.Id);
        }

        /// <summary>
        /// Updates the tag repository with any tag enabled properties and their values
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="tagRepo"></param>
        protected void UpdatePropertyTags(IContentBase entity, ITagRepository tagRepo)
        {
            foreach (var tagProp in entity.Properties.Where(x => x.TagSupport.Enable))
            {
                if (tagProp.TagSupport.Behavior == PropertyTagBehavior.Remove)
                {
                    //remove the specific tags
                    tagRepo.RemoveTagsFromProperty(
                        entity.Id,
                        tagProp.PropertyTypeId,
                        tagProp.TagSupport.Tags.Select(x => new Tag { Text = x.Item1, Group = x.Item2 }));
                }
                else
                {
                    //assign the tags
                    tagRepo.AssignTagsToProperty(
                        entity.Id,
                        tagProp.PropertyTypeId,
                        tagProp.TagSupport.Tags.Select(x => new Tag { Text = x.Item1, Group = x.Item2 }),
                        tagProp.TagSupport.Behavior == PropertyTagBehavior.Replace);
                }
            }
        }

        private Sql GetFilteredSqlForPagedResults(Sql sql, Func<Tuple<string, object[]>> defaultFilter = null)
        {
            //copy to var so that the original isn't changed
            var filteredSql = new Sql(sql.SQL, sql.Arguments);
            // Apply filter
            if (defaultFilter != null)
            {
                var filterResult = defaultFilter();
                filteredSql.Append(filterResult.Item1, filterResult.Item2);
            }
            return filteredSql;
        }

        private Sql GetSortedSqlForPagedResults(Sql sql, Direction orderDirection, string orderBy)
        {
            //copy to var so that the original isn't changed
            var sortedSql = new Sql(sql.SQL, sql.Arguments);
            // Apply order according to parameters
            if (string.IsNullOrEmpty(orderBy) == false)
            {
                var orderByParams = new[] { GetDatabaseFieldNameForOrderBy(orderBy) };
                if (orderDirection == Direction.Ascending)
                {
                    sortedSql.OrderBy(orderByParams);
                }
                else
                {
                    sortedSql.OrderByDescending(orderByParams);
                }
                return sortedSql;
            }
            return sortedSql;
        }


        protected IEnumerable<TEntity> GetPagedResultsByQuery<TDto, TContentBase>(IQuery<TEntity> query, long pageIndex, int pageSize, out long totalRecords,
                Tuple<string, string> nodeIdSelect,
                Func<List<TDto>, IEnumerable<TEntity>> processQuery,
                string orderBy,
                Direction orderDirection,
                Func<Tuple<string, object[]>> defaultFilter = null)
            where TContentBase : class, IAggregateRoot, TEntity
        {
            if (orderBy == null) throw new ArgumentNullException("orderBy");

            // Get base query
            var sqlBase = GetBaseQuery(false);

            if (query == null) query = Query;
            var translator = new SqlTranslator<TEntity>(sqlBase, query);
            var sqlQuery = translator.Translate();

            var sqlNodeIds = sqlQuery;

            //get sorted and filtered sql
            var sqlNodeIdsWithSort = GetSortedSqlForPagedResults(
                GetFilteredSqlForPagedResults(sqlNodeIds, defaultFilter),
                orderDirection, orderBy);

            // Get page of results and total count
            var pagedResult = Database.Page<TDto>(pageIndex + 1, pageSize, sqlNodeIdsWithSort);
            totalRecords = Convert.ToInt32(pagedResult.TotalItems);

            return processQuery(pagedResult.Items);
        }
        
        /// <summary>
         /// A helper method for inheritors to get the paged results by query in a way that minimizes queries
         /// </summary>
         /// <typeparam name="TDto">The type of the d.</typeparam>
         /// <typeparam name="TContentBase">The 'true' entity type (i.e. Content, Member, etc...)</typeparam>
         /// <param name="query">The query.</param>
         /// <param name="pageIndex">Index of the page.</param>
         /// <param name="pageSize">Size of the page.</param>
         /// <param name="totalRecords">The total records.</param>
         /// <param name="nodeIdSelect">The tablename + column name for the SELECT statement fragment to return the node id from the query</param>
         /// <param name="defaultFilter">A callback to create the default filter to be applied if there is one</param>
         /// <param name="processQuery">A callback to process the query result</param>
         /// <param name="orderBy">The order by column</param>
         /// <param name="orderDirection">The order direction.</param>
         /// <returns></returns>
         /// <exception cref="System.ArgumentNullException">orderBy</exception>
        protected IEnumerable<TEntity> GetPagedResultsByQuery<TDto, TContentBase>(IQuery<TEntity> query, long pageIndex, int pageSize, out long totalRecords,
            Tuple<string, string> nodeIdSelect,
            Func<Sql, IEnumerable<TEntity>> processQuery,
            string orderBy,
            Direction orderDirection,
            Func<Tuple<string, object[]>> defaultFilter = null)
            where TContentBase : class, IAggregateRoot, TEntity
        {
            if (orderBy == null) throw new ArgumentNullException("orderBy");

            // Get base query
            var sqlBase = GetBaseQuery(false);

            if (query == null) query = Query;
            var translator = new SqlTranslator<TEntity>(sqlBase, query);
            var sqlQuery = translator.Translate();

            // Note we can't do multi-page for several DTOs like we can multi-fetch and are doing in PerformGetByQuery,
            // but actually given we are doing a Get on each one (again as in PerformGetByQuery), we only need the node Id.
            // So we'll modify the SQL.
            // fixme.npoco - bah?
            /* generates
            SELECT * FROM (SELECT poco_base.*, ROW_NUMBER() OVER (ORDER BY LoginName) poco_rn
                FROM (
                    SELECT cmsMember.nodeId ====> MISSING!!! kills ORDER BY above!!!, cmsMember.LoginName
                        FROM [cmsMember]
                        INNER JOIN [cmsContentVersion] ON [cmsContentVersion].[ContentId] = [cmsMember].[nodeId]
                        INNER JOIN [cmsContent] ON [cmsContentVersion].[ContentId] = [cmsContent].[nodeId]
                        INNER JOIN [cmsContentType] ON [cmsContentType].[nodeId] = [cmsContent].[contentType]
                        INNER JOIN [umbracoNode] ON [cmsContent].[nodeId] = [umbracoNode].[id]
                    WHERE (([umbracoNode].[nodeObjectType] = '39EB0F98-B348-42A1-8662-E7EB18487560'))
                ) poco_base ) poco_paged
                WHERE poco_rn > 0 AND poco_rn <= 1000
                ORDER BY poco_rn
            */
            /*
            var sqlNodeIds = new Sql(
                sqlQuery.SQL.Replace("SELECT *", string.Format("SELECT {0}.{1}",nodeIdSelect.Item1, nodeIdSelect.Item2)),
                sqlQuery.Arguments);
            */
            var sqlNodeIds = sqlQuery;

            //get sorted and filtered sql
            var sqlNodeIdsWithSort = GetSortedSqlForPagedResults(
                GetFilteredSqlForPagedResults(sqlNodeIds, defaultFilter),
                orderDirection, orderBy);

            // Get page of results and total count
            IEnumerable<TEntity> result;
            var pagedResult = Database.Page<TDto>(pageIndex + 1, pageSize, sqlNodeIdsWithSort);
            totalRecords = Convert.ToInt32(pagedResult.TotalItems);

            // fixme.npoco - wtf are we doing here?!

            //NOTE: We need to check the actual items returned, not the 'totalRecords', that is because if you request a page number
            // that doesn't actually have any data on it, the totalRecords will still indicate there are records but there are none in
            // the pageResult, then the GetAll will actually return ALL records in the db.
            if (pagedResult.Items.Any())
            {
                //Crete the inner paged query that was used above to get the paged result, we'll use that as the inner sub query
                var args = sqlNodeIdsWithSort.Arguments;
                string sqlStringCount, sqlStringPage;
                Database.BuildPageQueries<TDto>(pageIndex * pageSize, pageSize, sqlNodeIdsWithSort.SQL, ref args, out sqlStringCount, out sqlStringPage);

                //if this is for sql server, the sqlPage will start with a SELECT * but we don't want that, we only want to return the nodeId
                sqlStringPage = sqlStringPage
                    .Replace("SELECT *",
                        //This ensures we only take the field name of the node id select and not the table name - since the resulting select
                        // will ony work with the field name.
                        "SELECT " + nodeIdSelect.Item2);

                //We need to make this an inner join on the paged query
                var splitQuery = sqlQuery.SQL.Split(new[] {"WHERE "}, StringSplitOptions.None);
                var withInnerJoinSql = new Sql(splitQuery[0])
                    .Append("INNER JOIN (")
                    //join the paged query with the paged query arguments
                    .Append(sqlStringPage, args)
                    .Append(") temp ")
                    .Append(string.Format("ON {0}.{1} = temp.{1}", nodeIdSelect.Item1, nodeIdSelect.Item2))
                    //add the original where clause back with the original arguments
                    .Where(splitQuery[1], sqlQuery.Arguments);

                //get sorted and filtered sql
                var fullQuery = GetSortedSqlForPagedResults(
                    GetFilteredSqlForPagedResults(withInnerJoinSql, defaultFilter),
                    orderDirection, orderBy);

                var content = processQuery(fullQuery)
                    .Cast<TContentBase>()
                    .AsQueryable();

                // Now we need to ensure this result is also ordered by the same order by clause
                var orderByProperty = GetEntityPropertyNameForOrderBy(orderBy);
                if (orderDirection == Direction.Ascending)
                {
                    result = content.OrderBy(orderByProperty);
                }
                else
                {
                    result = content.OrderByDescending(orderByProperty);
                }
            }
            else
            {
                result = Enumerable.Empty<TEntity>();
            }

            return result;
        }

        protected IDictionary<int, PropertyCollection> GetPropertyCollection(DocumentDefinition[] ddefs)
        {
            var versions = ddefs.Select(x => x.Version).ToArray();
            if (versions.Length == 0) return new Dictionary<int, PropertyCollection>();

            var sql = new Sql()
                .Select<PropertyDataDto>(r => r.Select<PropertyTypeDto>())
                .From<PropertyDataDto>(SqlSyntax)
                .LeftJoin<PropertyTypeDto>(SqlSyntax).On<PropertyDataDto, PropertyTypeDto>(SqlSyntax, left => left.PropertyTypeId, right => right.Id)
                .WhereIn<PropertyDataDto>(SqlSyntax, x => x.VersionId, versions);

            // fixme.npoco - obsolete version LEFT OUTER JOIN to prevalues, why?!

            // fetch by version only, that should be enough, versions are guids and the same guid
            // should not be reused for two different nodes -- then validate with a Where() just
            // to be sure -- but we probably can get rid of the validation
            var allPropertyData = Database
                .Fetch<PropertyDataDto>(sql)
                .Where(x => ddefs.Any(y => y.Version == x.VersionId && y.Id == x.NodeId)) // so... probably redundant, but safe
                .ToList();

            // lazy access to prevalue for data types if any property requires tag support
            var pre = new Lazy<IEnumerable<DataTypePreValueDto>>(() =>
            {
                var allPropertyTypes = allPropertyData
                    .Select(x => x.PropertyTypeDto)
                    .DistinctBy(x => x.Id)
                    .ToList();

                var preSql = new Sql()
                    .Select<DataTypePreValueDto>()
                    .From<DataTypePreValueDto>(SqlSyntax)
                    .LeftJoin<PropertyTypeDto>(SqlSyntax).On<DataTypePreValueDto, PropertyTypeDto>(SqlSyntax, left => left.DataTypeNodeId, right => right.DataTypeId)
                    .WhereIn<PropertyTypeDto>(SqlSyntax, x => x.Id, allPropertyTypes);

                return Database.Fetch<DataTypePreValueDto>(preSql);
            });

            return GetPropertyCollection(ddefs, allPropertyData, pre);
        }

        [Obsolete("WTF", true)]
        protected IDictionary<int, PropertyCollection> GetPropertyCollection(
            Sql docSql,
            IEnumerable<DocumentDefinition> documentDefs)
        {
            if (documentDefs.Any() == false) return new Dictionary<int, PropertyCollection>();

            //we need to parse the original SQL statement and reduce the columns to just cmsContent.nodeId, cmsContentVersion.VersionId so that we can use
            // the statement to go get the property data for all of the items by using an inner join
            var parsedOriginalSql = "SELECT {0} " +
                                    docSql.SQL.Substring(docSql.SQL.IndexOf("FROM", StringComparison.Ordinal));
            //now remove everything from an Orderby clause and beyond
            if (parsedOriginalSql.InvariantContains("ORDER BY "))
            {
                parsedOriginalSql = parsedOriginalSql.Substring(0,
                    parsedOriginalSql.LastIndexOf("ORDER BY ", StringComparison.Ordinal));
            }

            var propSql = new Sql(@"SELECT cmsPropertyData.*
FROM cmsPropertyData
INNER JOIN cmsPropertyType
ON cmsPropertyData.propertytypeid = cmsPropertyType.id
INNER JOIN
	(" + string.Format(parsedOriginalSql, "cmsContent.nodeId, cmsContentVersion.VersionId") + @") as docData
ON cmsPropertyData.versionId = docData.VersionId AND cmsPropertyData.contentNodeId = docData.nodeId
LEFT OUTER JOIN cmsDataTypePreValues
ON cmsPropertyType.dataTypeId = cmsDataTypePreValues.datatypeNodeId", docSql.Arguments);

            var allPropertyData = Database.Fetch<PropertyDataDto>(propSql);

            //This is a lazy access call to get all prevalue data for the data types that make up all of these properties which we use
            // below if any property requires tag support
            var allPreValues = new Lazy<IEnumerable<DataTypePreValueDto>>(() =>
            {
                var preValsSql = new Sql(@"SELECT a.id, a.value, a.sortorder, a.alias, a.datatypeNodeId
FROM cmsDataTypePreValues a
WHERE EXISTS(
    SELECT DISTINCT b.id as preValIdInner
    FROM cmsDataTypePreValues b
	INNER JOIN cmsPropertyType
	ON b.datatypeNodeId = cmsPropertyType.dataTypeId
    INNER JOIN
	    (" + string.Format(parsedOriginalSql, "DISTINCT cmsContent.contentType") + @") as docData
    ON cmsPropertyType.contentTypeId = docData.contentType
    WHERE a.id = b.id)", docSql.Arguments);

                return Database.Fetch<DataTypePreValueDto>(preValsSql);
            });

            return GetPropertyCollection(documentDefs.ToArray(), allPropertyData, allPreValues);
        }

        protected IDictionary<int, PropertyCollection> GetPropertyCollection(DocumentDefinition[] documentDefs, List<PropertyDataDto> allPropertyData, Lazy<IEnumerable<DataTypePreValueDto>> allPreValues)
        {
            var result = new Dictionary<int, PropertyCollection>();

            var propertiesWithTagSupport = new Dictionary<string, SupportTagsAttribute>();

            //iterate each definition grouped by it's content type - this will mean less property type iterations while building
            // up the property collections
            foreach (var compositionGroup in documentDefs.GroupBy(x => x.Composition))
            {
                var compositionProperties = compositionGroup.Key.CompositionPropertyTypes.ToArray();

                foreach (var def in compositionGroup)
                {
                    var propertyDataDtos = allPropertyData.Where(x => x.NodeId == def.Id).Distinct();

                    var propertyFactory = new PropertyFactory(compositionProperties, def.Version, def.Id, def.CreateDate, def.VersionDate);
                    var properties = propertyFactory.BuildEntity(propertyDataDtos.ToArray()).ToArray();

                    var newProperties = properties.Where(x => x.HasIdentity == false && x.PropertyType.HasIdentity);

                    foreach (var property in newProperties)
                    {
                        var propertyDataDto = new PropertyDataDto { NodeId = def.Id, PropertyTypeId = property.PropertyTypeId, VersionId = def.Version };
                        int primaryKey = Convert.ToInt32(Database.Insert(propertyDataDto));

                        property.Version = def.Version;
                        property.Id = primaryKey;
                    }

                    foreach (var property in properties)
                    {
                        //NOTE: The benchmarks run with and without the following code show very little change so this is not a perf bottleneck
                        var editor = PropertyEditorResolver.Current.GetByAlias(property.PropertyType.PropertyEditorAlias);

                        var tagSupport = propertiesWithTagSupport.ContainsKey(property.PropertyType.PropertyEditorAlias)
                            ? propertiesWithTagSupport[property.PropertyType.PropertyEditorAlias]
                            : TagExtractor.GetAttribute(editor);

                        if (tagSupport != null)
                        {
                            //add to local cache so we don't need to reflect next time for this property editor alias
                            propertiesWithTagSupport[property.PropertyType.PropertyEditorAlias] = tagSupport;

                            //this property has tags, so we need to extract them and for that we need the prevals which we've already looked up
                            var preValData = allPreValues.Value.Where(x => x.DataTypeNodeId == property.PropertyType.DataTypeDefinitionId)
                                .Distinct()
                                .ToArray();

                            var asDictionary = preValData.ToDictionary(x => x.Alias, x => new PreValue(x.Id, x.Value, x.SortOrder));

                            var preVals = new PreValueCollection(asDictionary);

                            var contentPropData = new ContentPropertyData(property.Value,
                                preVals,
                                new Dictionary<string, object>());

                            TagExtractor.SetPropertyTags(property, contentPropData, property.Value, tagSupport);
                        }
                    }

                    if (result.ContainsKey(def.Id))
                    {
                        Logger.Warn<VersionableRepositoryBase<TId, TEntity>>("The query returned multiple property sets for document definition " + def.Id + ", " + def.Composition.Name);
                    }
                    result[def.Id] = new PropertyCollection(properties);
                }
            }

            return result;
        }

        public class DocumentDefinition
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="T:System.Object"/> class.
            /// </summary>
            public DocumentDefinition(int id, Guid version, DateTime versionDate, DateTime createDate, IContentTypeComposition composition)
            {
                Id = id;
                Version = version;
                VersionDate = versionDate;
                CreateDate = createDate;
                Composition = composition;
            }

            public int Id { get; set; }
            public Guid Version { get; set; }
            public DateTime VersionDate { get; set; }
            public DateTime CreateDate { get; set; }
            public IContentTypeComposition Composition { get; set; }
        }

        protected virtual string GetDatabaseFieldNameForOrderBy(string orderBy)
        {
            // Translate the passed order by field (which were originally defined for in-memory object sorting
            // of ContentItemBasic instances) to the database field names.
            switch (orderBy.ToUpperInvariant())
            {
                case "UPDATEDATE":
                    return "cmsContentVersion.VersionDate";
                case "NAME":
                    return "umbracoNode.text";
                case "OWNER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return "umbracoNode.nodeUser";
                case "PATH":
                    return "umbracoNode.path";
                default:
                    //ensure invalid SQL cannot be submitted
                    return Regex.Replace(orderBy, @"[^\w\.,`\[\]@-]", "");
            }
        }

        protected virtual string GetEntityPropertyNameForOrderBy(string orderBy)
        {
            // Translate the passed order by field (which were originally defined for in-memory object sorting
            // of ContentItemBasic instances) to the IMedia property names.
            switch (orderBy.ToUpperInvariant())
            {
                case "OWNER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return "CreatorId";
                case "UPDATER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return "WriterId";
                case "VERSIONDATE":
                    return "UpdateDate";
                default:
                    //ensure invalid SQL cannot be submitted
                    return Regex.Replace(orderBy, @"[^\w\.,`\[\]@-]", "");
            }
        }

        /// <summary>
        /// Deletes all media files passed in.
        /// </summary>
        /// <param name="files"></param>
        /// <returns></returns>
        public virtual bool DeleteMediaFiles(IEnumerable<string> files)
        {
            //ensure duplicates are removed
            files = files.Distinct();

            var allsuccess = true;

            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();
            Parallel.ForEach(files, file =>
            {
                try
                {
                    if (file.IsNullOrWhiteSpace()) return;

                    var relativeFilePath = fs.GetRelativePath(file);
                    if (fs.FileExists(relativeFilePath) == false) return;

                    var parentDirectory = System.IO.Path.GetDirectoryName(relativeFilePath);

                    // don't want to delete the media folder if not using directories.
                    if (_contentSection.UploadAllowDirectories && parentDirectory != fs.GetRelativePath("/"))
                    {
                        //issue U4-771: if there is a parent directory the recursive parameter should be true
                        fs.DeleteDirectory(parentDirectory, String.IsNullOrEmpty(parentDirectory) == false);
                    }
                    else
                    {
                        fs.DeleteFile(file, true);
                    }
                }
                catch (Exception e)
                {
                    Logger.Error<VersionableRepositoryBase<TId, TEntity>>("An error occurred while deleting file attached to nodes: " + file, e);
                    allsuccess = false;
                }
            });

            return allsuccess;
        }
    }
}