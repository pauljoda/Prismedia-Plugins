namespace Prismedia.Plugin.Suwayomi;

/// <summary>Version-pinned GraphQL documents for the supported Suwayomi server release.</summary>
internal static class SuwayomiQueries {
    #region Static Variables
    internal const string About = "query PrismediaAbout { aboutServer { name version } }";
    internal const string Sources = "query PrismediaSources($first:Int!,$offset:Int!){ sources(first:$first,offset:$offset,order:[{by:NAME,byType:ASC}]){ nodes{id name lang contentWarning} totalCount } }";
    internal const string Source = "query PrismediaSource($id:LongString!){ source(id:$id){id name lang contentWarning} }";
    internal const string FetchSourceManga = "mutation PrismediaSourceManga($source:LongString!,$type:FetchSourceMangaType!,$page:Int!,$query:String){ fetchSourceManga(input:{source:$source,type:$type,page:$page,query:$query}){ mangas{id sourceId url title author artist description initialized inLibrary chaptersLastFetchedAt} hasNextPage } }";
    internal const string Manga = "query PrismediaManga($id:Int!){ mangas(condition:{id:$id},first:2){ nodes{id sourceId url title author artist description initialized inLibrary chaptersLastFetchedAt} totalCount } }";
    internal const string Chapters = "query PrismediaChapters($mangaId:Int!,$first:Int!,$offset:Int!){ chapters(condition:{mangaId:$mangaId},first:$first,offset:$offset,order:[{by:SOURCE_ORDER,byType:ASC}]){ nodes{id url name uploadDate chapterNumber scanlator mangaId sourceOrder realUrl isDownloaded pageCount} totalCount } }";
    internal const string RefreshNonLibraryManga = "mutation PrismediaRefreshManga($id:Int!){ fetchMangaAndChapters(input:{id:$id,fetchManga:true,fetchChapters:true}){ manga{id sourceId url title author artist description initialized inLibrary chaptersLastFetchedAt} chapters{id} } }";
    internal const string Exact = "query PrismediaExact($sourceId:LongString!,$mangaId:Int!,$chapterId:Int!){ source:source(id:$sourceId){id name lang contentWarning} manga(id:$mangaId){id sourceId url title author artist description initialized inLibrary chaptersLastFetchedAt} chapter(id:$chapterId){id url name uploadDate chapterNumber scanlator mangaId sourceOrder realUrl isDownloaded pageCount} downloadStatus{queue{state progress tries position chapter{id url name uploadDate chapterNumber scanlator mangaId sourceOrder realUrl isDownloaded pageCount}}} }";
    internal const string Enqueue = "mutation PrismediaEnqueue($id:Int!,$clientMutationId:String!){ enqueueChapterDownload(input:{id:$id,clientMutationId:$clientMutationId}){clientMutationId downloadStatus{queue{state progress tries position chapter{id url name uploadDate chapterNumber scanlator mangaId sourceOrder realUrl isDownloaded pageCount}}}} }";
    #endregion
}
