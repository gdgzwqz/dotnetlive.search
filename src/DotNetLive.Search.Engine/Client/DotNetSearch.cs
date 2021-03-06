﻿using Nest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using DotNetLive.Search.Engine.Logger;
using Elasticsearch.Net;
using DotNetLive.Search.Engine.Config;
using Microsoft.Extensions.Logging;
using DotNetLive.Search.Engine.Model;
using DotNetLive.Search.Config;

namespace DotNetLive.Search.Engine.Client
{
    public partial class DotNetSearch
    {
        private string _defaultIndex;
        private SearchEngineBuilder _builder;
        private ILogger _logger;

        #region 构造函数

        public DotNetSearch()
        {
            _builder = new SearchEngineBuilder();
        }

        public DotNetSearch(ElasticSetting settting)
        {
            _builder = new SearchEngineBuilder(settting);
        }

        public DotNetSearch(ILogger logger) : this()
        {
            _logger = logger;
        }
        #endregion

        #region 设置
        /// <summary>
        /// 设置索引
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public DotNetSearch UseIndex(string index)
        {
            _defaultIndex = index;
            return this;
        }

        /// <summary>
        /// 设置日志
        /// </summary>
        /// <param name="logger"></param>
        /// <returns></returns>
        public DotNetSearch UseLogger(ILogger logger)
        {
            _logger = logger;
            return this;
        }

        #endregion

        #region 增删改查
        /// <summary>
        /// 新增一条文档
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="model">文档实体</param>
        /// <param name="index">文档所在库</param>
        /// <returns>返回true false</returns>
        public bool Index<T>(T model, string index = null) where T : class => HandleResponseResult(() =>
        {
            IIndexResponse response = _builder?.Client?.Index(model, x => x.Type(typeof(T).SearchName()).Index(index ?? _defaultIndex));
            return response;
        });

        /// <summary>
        /// 更新一条文档
        /// 由于是普通的简单更新，当id已经存在时，则会更新文档，所以这里直接调用index方法，（复杂方法待研究）
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="model">文档实体</param>
        /// <param name="index">文档所在库</param>
        /// <returns>返回true false</returns>
        public bool Update<T>(T model, string index = null) where T : class
        {
            return Index(model, index);
        }

        /// <summary>
        /// 删除一条文档
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="id">主键id</param>
        /// <param name="index">文档所在库</param>
        /// <returns>返回true false</returns>
        public bool Delete<T>(long id, string index = null) where T : class => HandleResponseResult(() =>
        {
            //DocumentPath<T>.Id(id)
            IDeleteResponse response = _builder?.Client.Delete<T>(id, x => x.Type(typeof(T).SearchName()).Index(index ?? _defaultIndex));
            return response;
        });

        /// <summary>
        /// 查询一条文档
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="id">文档id</param>
        /// <param name="index">文档所在库</param>
        /// <returns>返回该文档</returns>
        public T Query<T>(long id, string index = null) where T : class
        {
            IGetResponse<T> response = _builder?.Client.Get<T>(id, x => x.Type(typeof(T).SearchName()).Index(index ?? _defaultIndex));
            var t = response?.Source;
            if (t == null)
            {
                if (_logger != null)
                {
                    _logger.LogInformation(response.ApiCall.DebugInformation);
                }
            }
            return t;
        }

        /// <summary>
        /// 方法有点长，需要重构
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="pageParams"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public IQueryResult<T> Query<T>(IPageParam pageParams, string index = null) where T : class
        {
            if (pageParams == null)
            {
                pageParams = new PageParam
                {
                    PageIndex = 1,
                    PageSize = 20
                };
            }

            SearchDescriptor<T> searchDescriptor = new SearchDescriptor<T>()
                     .Type(typeof(T).SearchName())
                     .Index(index ?? _defaultIndex)
                     .From(pageParams.From)
                     .Size(pageParams.PageSize);

            
            if (pageParams is PageParamWithSearch)
            {
                PageParamWithSearch pageParamsSearch = pageParams as PageParamWithSearch;

                searchDescriptor = searchDescriptor.Query(q =>
                    q.QueryString(qs =>
                        qs.Fields(pageParamsSearch.SearchKeys)
                          .Query(pageParamsSearch.KeyWord)
                          .DefaultOperator(pageParamsSearch.Operator)));
            }else if (pageParams is PageParam)
            {
                searchDescriptor = searchDescriptor.Query(q =>
                    q.QueryString(qs =>
                        qs.Query(pageParams.KeyWord)
                          .DefaultOperator(pageParams.Operator)));
            }
            //是否需要高亮
            bool hasHighlight = pageParams.Highlight?.Keys?.Length > 0;
            if (hasHighlight) {
                //TODO
                BuildHighLightQuery<T>(pageParams, ref searchDescriptor);
            }
            //所有条件配置完成之后执行查询
            ISearchResponse<T> response = _builder?.Client.Search<T>(s => searchDescriptor);

            var list = response.Documents;
            if (hasHighlight) {
               var  listWithHightlight = new List<T>();
                response.Hits.ToList().ForEach(x =>
                {
                    if (x.Highlights?.Count > 0)
                    {
                        PropertyInfo[] properties = typeof(T).GetProperties();
                        foreach (string key in pageParams.Highlight?.Keys) {
                            //先得到要替换的内容
                            if (x.Highlights.ContainsKey(key))
                            {
                                string value = string.Join("", x.Highlights[key]?.Highlights);
                                PropertyInfo info = properties.FirstOrDefault(p => p.Name == pageParams.Highlight.PrefixOfKey + key);
                                //没找到带前缀的属性，则替换之前的
                                if (info == null && pageParams.Highlight.ReplaceAuto)
                                {
                                    info = properties.FirstOrDefault(p => p.Name == key);
                                }
                                if (info?.CanWrite == true)
                                {
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        //如果高亮字段不为空，才赋值，否则就赋值成空
                                        info.SetValue(x.Source, value);
                                    }
                                }
                            }
                        }
                    }
                    listWithHightlight.Add(x.Source);
                });
            }

            IQueryResult<T> result = new CustomQueryResult<T>
            {
                List = list,
                Took = response.Took,
                Total = response.Total
            };
            return result;
        }

        /// <summary>
        /// 私有方法，构造高亮查询
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="pageParams"></param>
        /// <param name="searchDescriptor"></param>
        private void BuildHighLightQuery<T>(IPageParam pageParams, ref SearchDescriptor<T> searchDescriptor) where T:class
        {
            int keysLength = (pageParams.Highlight?.Keys?.Length).Value;
            Func<HighlightFieldDescriptor<T>, IHighlightField>[] filedDesciptor = new Func<HighlightFieldDescriptor<T>, IHighlightField>[keysLength];
            int keysIndex = 0;
            foreach (string key in pageParams.Highlight?.Keys)
            {
                filedDesciptor[keysIndex] = hf => hf.Field(key)//简介高亮
                                                    .HighlightQuery(q => q
                                                    .Match(m => m
                                                    .Field(key)
                                                    .Query(pageParams.KeyWord)));
                keysIndex++;
            }
            //构造hightlight
            IHighlight highLight = new HighlightDescriptor<T>()
                .PreTags(pageParams.Highlight.PreTags)
                .PostTags(pageParams.Highlight.PostTags)
                .Fields(filedDesciptor);
            //设置高亮
            searchDescriptor = searchDescriptor.Highlight(s => highLight);
        }
        #endregion

        #region 其他查询
        #region 单一条件查询，一般是精确查询
        /// <summary>
        /// 单一条件查询，一般是精确查询
        /// exp: id=123456,name="zhangsan",
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="field">字段名</param>
        /// <param name="value">查询值</param>
        /// <param name="index">索引</param>
        /// <returns></returns>
        public IEnumerable<T> Query<T>(string field, object value, string index = null) where T : class
        {
            if (string.IsNullOrEmpty(field))
            {
                return null;
            }
            ISearchRequest searchRequest = new SearchDescriptor<T>().Index(index ?? _defaultIndex).PostFilter(f => f.Term(x => x.Field(field).Value(value)));

            var response = _builder?.Client.Search<T>(searchRequest);
            return response.Documents;
        }
        #endregion

        #region  根据ID数组查询
        /// <summary>
        /// 根据ID数组查询
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="index">文档索引</param>
        /// <param name="ids">文档值</param>
        /// <returns></returns>
        public IEnumerable<T> Query<T>(string index = null, params long[] ids) where T : class
        {
            ISearchRequest searchRequest = CreateDescriptor<T>(index).Query(q => q.Ids(x => x.Values(ids)));
            return Query<T>(searchRequest);
        }
        public IEnumerable<T> Query<T>(string index = null, params string[] ids) where T : class
        {
            ISearchRequest searchRequest = CreateDescriptor<T>(index).Query(q => q.Ids(x => x.Values(ids)));
            return Query<T>(searchRequest);
        }
        public IEnumerable<T> Query<T>(string index = null, params Guid[] ids) where T : class
        {
            ISearchRequest searchRequest = CreateDescriptor<T>(index).Query(q => q.Ids(x => x.Values(ids)));
            return Query<T>(searchRequest);
        }

        #endregion

        #region private
        private IEnumerable<T> Query<T>(ISearchRequest request) where T : class
        {
            var response = _builder?.Client.Search<T>(request);
            return response.Documents;
        }
        private SearchDescriptor<T> CreateDescriptor<T>(string index) where T : class
        {
            return new SearchDescriptor<T>().Index(index ?? _defaultIndex);
        }
        #endregion
        #endregion

        #region 批量操作

        #region 批量添加或者更新
        /// <summary>
        /// 批量添加或者更新文档
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="documents">将要添加或者更新的文档集合</param>
        /// <param name="index">文档所在库</param>
        /// <returns>返回更新的条数</returns>
        public int IndexMany<T>(IEnumerable<T> documents, string index = null) where T : class
        {
            if (documents == null || documents.Count() == 0) { return 0; }
            IBulkResponse response = _builder?.Client.IndexMany<T>(documents, index ?? _defaultIndex, typeof(T).SearchName());
            if (response.Errors)
            {
                if (_logger != null)
                {
                    _logger.LogInformation(response.DebugInformation);
                    //_logger.Error(response.ItemsWithErrors)
                }

            }
            return response.Items.Count;

        }
        /// <summary>
        /// 批量添加或者更新文档
        /// </summary>
        /// <typeparam name="T">文档类型</typeparam>
        /// <param name="documents">将要添加或者更新的文档集合</param>
        /// <param name="index">文档所在库</param>
        /// <returns>返回更新的条数</returns>
        public int Bulk<T>(IEnumerable<T> objects, string index = null) where T : class
        {
            BulkDescriptor descriptor = new BulkDescriptor();
            descriptor.Index(index ?? _defaultIndex).Type(typeof(T).SearchName()).IndexMany(objects);
            IBulkResponse response = _builder?.Client.Bulk(descriptor);
            if (response.Errors)
            {
                if (_logger != null)
                {
                    _logger.LogInformation(response.DebugInformation);
                    //_logger.Error(response.ItemsWithErrors)
                }

            }
            return response.Items.Count;
        }
        #endregion 

        #endregion

        #region 请求结果统一处理
        private bool HandleResponseResult(Func<IBodyWithApiCallDetails> handler)
        {
            var response = handler();

            if (response.ApiCall.Success)
            {
                return true;
            }

            if (_logger != null)
            {
                //统一处理日志
                _logger.LogInformation(response.ApiCall.DebugInformation);
            }
            return false;
        }
        #endregion
    }
}
