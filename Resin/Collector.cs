﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using log4net;
using Resin.Analysis;
using Resin.IO;
using Resin.IO.Read;
using Resin.Querying;
using Resin.Sys;

namespace Resin
{
    public class Collector : IDisposable
    {
        private readonly string _directory;
        private static readonly ILog Log = LogManager.GetLogger(typeof(Collector));
        private readonly IxInfo _ix;
        private readonly IDictionary<Term, IList<DocumentPosting>> _termCache;
        private readonly IScoringScheme _scorer;

        public Collector(string directory, IxInfo ix, IScoringScheme scorer)
        {
            _directory = directory;
            _ix = ix;
            _termCache = new Dictionary<Term, IList<DocumentPosting>>();
            _scorer = scorer;
        }

        public IEnumerable<DocumentScore> Collect(QueryContext query)
        {
            Scan(query);
            GetPostings(query);

            return query.Reduce().Select(p=>p.Score).OrderByDescending(s => s.Score);
        }

        private void Scan(QueryContext query)
        {
            if (query == null) throw new ArgumentNullException("query");

            var time = Time();

            //foreach (var q in new List<QueryContext> { query }.Concat(query.Children))
            //{
            //    DoScan(q);
            //}
            Parallel.ForEach(new List<QueryContext> {query}.Concat(query.Children), DoScan);

            Log.DebugFormat("total scan time for {0}: {1}", query, time.Elapsed);
        }

        private void DoScan(QueryContext query)
        {
            var terms = new List<Term>();
            //var terms = new ConcurrentBag<Term>();
            var readers = GetTreeReaders(query.Field).ToList();

            foreach (var reader in readers)
            //Parallel.ForEach(readers, reader =>
            {
                var time = Time();

                using (reader)
                {
                    if (query.Fuzzy)
                    {
                        terms.AddRange(reader.Near(query.Value, query.Edits).Select(word => new Term(query.Field, word)));
                    }
                    else if (query.Prefix)
                    {
                        terms.AddRange(reader.StartsWith(query.Value).Select(word => new Term(query.Field, word)));
                    }
                    else if (reader.HasWord(query.Value))
                    {
                        terms.Add(new Term(query.Field, new Word(query.Value)));
                    }
                }

                Log.DebugFormat("scanned {0} {1} in\t{2}", query.AsReadable(), reader.FileName, time.Elapsed);
            }//);
            
            query.Terms = terms;
        }

        private void GetPostings(QueryContext query)
        {
            if (query == null) throw new ArgumentNullException("query");

            var time = Time();

            foreach (var q in new List<QueryContext> {query}.Concat(query.Children))
            {
                DoGetPostings(q);
            }

            Log.DebugFormat("read postings for {0} in {1}", query, time.Elapsed);
        }

        private void DoGetPostings(QueryContext query)
        {
            var result = DoReadPostings(query.Terms)
                .Aggregate<IEnumerable<DocumentPosting>, IEnumerable<DocumentPosting>>(
                    null, DocumentPosting.JoinOr);

            query.Postings = result ?? Enumerable.Empty<DocumentPosting>();
        }

        private IEnumerable<IEnumerable<DocumentPosting>> DoReadPostings(IEnumerable<Term> terms)
        {
            foreach(var term in terms)
            {
                IList<DocumentPosting> postings;

                if (!_termCache.TryGetValue(term, out postings))
                {
                    postings = GetPostingsReader(term).Read(term).ToList();
                    postings = Score(postings).ToList();
                    _termCache.Add(term, postings);
                }

                yield return postings;
            }
        }

        private PostingsReader GetPostingsReader(Term term)
        {
            var fileId = term.ToPostingsFileId();
            var fileName = Path.Combine(_directory, fileId + ".pos");
            var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var sr = new StreamReader(fs, Encoding.ASCII);

            return new PostingsReader(sr);
        }

        private IEnumerable<DocumentPosting> Score(IEnumerable<DocumentPosting> postings)
        {
            foreach (var posting in postings)
            {
                var scorer = _scorer.CreateScorer(_ix.DocumentCount.DocCount[posting.Field], posting.Count);

                posting.Score = new DocumentScore(posting.DocumentId, posting.Count);

                scorer.Score(posting.Score);

                yield return posting;
            }
        }

        private IEnumerable<LcrsTreeReader> GetTreeReaders(string field)
        {
            var searchPattern = string.Format("{0}*.tri", field.ToTrieFileId());
            var files = Directory.GetFiles(_directory, searchPattern);
            return files.OrderBy(f=>f).Select(fileName => new LcrsTreeReader(fileName));
        }

        private static Stopwatch Time()
        {
            var timer = new Stopwatch();
            timer.Start();
            return timer;
        }

        public void Dispose()
        {
        }
    }
}