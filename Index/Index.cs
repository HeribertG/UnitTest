using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Microsoft.OpenApi.Models;
using UnitTest.FakeData;
using static UnitTest.FakeData.FakeMockaroo;

namespace UnitTest.Index
{
  [TestFixture]
  internal class Index
  {
    //[Test]
    //public void CreateIndexTests()
    //{
    //  //Arrange
    //  var maxResult = 20;
    // var textSearch = "Kennett";
    //  var repository = FakeMockaroo.FakeClient();
    //  var directory = new RAMDirectory();
    //  var analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);

    //  using (var indexWriter = new IndexWriter(directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)))
    //  {
    //    foreach (var item in repository)
    //    {
    //      var document = new Document();
    //      document.Add(new StringField("Id", item.Id.ToString(), Field.Store.NO));
    //      document.Add(new StringField("First_name", item.First_name.Trim(), Field.Store.YES));
    //      document.Add(new StringField("Last_name", item.Last_name.Trim(), Field.Store.YES));
    //      document.Add(new StringField("Gender", item.Gender.Trim(), Field.Store.YES));
    //      document.Add(new StringField("Ip_address", item.Ip_address.Trim(), Field.Store.YES));
    //      indexWriter.AddDocument(document);
    //    }
    //    indexWriter.Commit();
    //    indexWriter.Flush(true, true);
    //  };

    //  // Erstelle einen QueryParser und einen Query
    //  var parser = new QueryParser(LuceneVersion.LUCENE_48, "First_name", analyzer);
    //  var query = parser.Parse(textSearch); // Ersetzen Sie "Suchbegriff" durch den Namen, den Sie suchen möchten

    //  // Erstelle einen IndexSearcher
    //  var searcher = new IndexSearcher(DirectoryReader.Open(directory));

    //  // Führe die Suche durch und erhalte die Top 10 Ergebnisse
    //  var hits = searcher.Search(query, 10).ScoreDocs;

    //  // Iteriere über die Ergebnisse und zeige sie an
    //  foreach (var hit in hits)
    //  {
    //    var hitDocument = searcher.Doc(hit.Doc);
    //    Console.WriteLine($"Id: {hitDocument.Get("Id")}, First_name: {hitDocument.Get("First_name")}, Score: {hit.Score}");
    //  }

    //  //Act
    //  //var arry = new List<string>() { "First_name", "Last_name", "Gender", "Ip_address" }.ToArray();
    //  //var queryParser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, arry, analyzer);
    //  //var query = queryParser.Parse(textSearch);
    //  //var reader = DirectoryReader.Open(directory);
    //  //var searcher = new IndexSearcher(reader);
    //  //ScoreDoc[] docs = searcher.Search(query, null, maxResult).ScoreDocs;
    //  //var collector = TopScoreDocCollector.Create(maxResult, true);
    //  //searcher.Search(query, collector);
    //  //var topDocs = collector.GetTopDocs();
    //  //foreach (var item in topDocs.ScoreDocs)
    //  //{
    //  //  //Assert

    //  //  var id = item.Doc;
    //  //  var result = searcher.Doc(id);
    //  //  var n = result.GetField("First_name").GetStringValue();
    //  //  result.Should().NotBeNull();
    //  //  //result.Should().Be(textSearch);
    //  //}
    // }

    [Test]
    public void OpenApiExample()
    {
      // Beispieldaten
      var repository = FakeMockaroo.FakeClient();
      var textSearch = "kennett";
      var maxResult = 20;

      var directory = new RAMDirectory();
      var analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);

      // Index erstellen
      using (var indexWriter = new IndexWriter(directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)))
      {
        foreach (var item in repository)
        {
          var document = new Document();
          document.Add(new StringField("Id", item.Id.ToString(), Field.Store.YES));
          document.Add(new TextField("First_name", item.First_name, Field.Store.YES));
          document.Add(new TextField("Last_name", item.Last_name, Field.Store.YES));
          document.Add(new TextField("Gender", item.Gender.Trim(), Field.Store.YES));
          document.Add(new TextField("Ip_address", item.Ip_address.Trim(), Field.Store.YES));
          indexWriter.AddDocument(document);
        }
        indexWriter.Commit();
      }

      // Suche
      var parser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, new[] { "Id", "First_name", "Last_name" }, analyzer);
      var query = parser.Parse(textSearch); // Ersetzen Sie "John" durch den Begriff, den Sie suchen möchten

      var searcher = new IndexSearcher(DirectoryReader.Open(directory));
      var hits = searcher.Search(query, 10).ScoreDocs;

      foreach (var hit in hits)
      {
        var hitDocument = searcher.Doc(hit.Doc);
        Console.WriteLine($"Id: {hitDocument.Get("Id")}, First_name: {hitDocument.Get("First_name")}, Last_name: {hitDocument.Get("Last_name")}, Score: {hit.Score}");
      }
    }
  }
}
