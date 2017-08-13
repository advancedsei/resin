﻿using DocumentTable;
using log4net;
using Resin.IO;
using Resin.IO.Read;
using StreamIndex;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Resin.Querying
{
    public abstract class Search
    {
        protected static readonly ILog Log = LogManager.GetLogger(typeof(Search));

        protected IReadSession Session { get; set; }
        protected IScoringSchemeFactory ScoringFactory { get; set; }
        protected PostingsReader PostingsReader { get; set; }

        protected Search(
            IReadSession session, IScoringSchemeFactory scoringFactory, PostingsReader postingsReader)
        {
            Session = session;
            ScoringFactory = scoringFactory;
            PostingsReader = postingsReader;
        }

        protected ITrieReader GetTreeReader(string field)
        {
            var key = field.ToHash();
            long offset;

            if (Session.Version.FieldOffsets.TryGetValue(key, out offset))
            {
                Session.Stream.Seek(offset, SeekOrigin.Begin);
                return new MappedTrieReader(Session.Stream);
            }
            return null;
        }

        protected IList<DocumentScore> Score(IList<DocumentPosting> postings)
        {
            var scoreTime = Stopwatch.StartNew();
            var scores = new List<DocumentScore>(postings.Count);

            if (postings != null && postings.Count > 0)
            {
                var docsWithTerm = postings.Count;
                var scorer = ScoringFactory.CreateScorer(Session.Version.DocumentCount, docsWithTerm);
                var postingsByDoc = postings.GroupBy(p => p.DocumentId);

                foreach (var posting in postingsByDoc)
                {
                    var docId = posting.Key;
                    var docHash = Session.ReadDocHash(docId);

                    if (!docHash.IsObsolete)
                    {
                        var score = scorer.Score(posting.Count());

                        scores.Add(new DocumentScore(docId, docHash.Hash, score, Session.Version));
                    }
                }
            }

            Log.DebugFormat("scored in {0}", scoreTime.Elapsed);

            return scores;
        }

        public IList<IList<DocumentPosting>> GetManyPostingsLists(IList<Term> terms)
        {
            var time = Stopwatch.StartNew();

            var addresses = new List<BlockInfo>(terms.Count);

            foreach (var term in terms)
            {
                addresses.Add(term.Word.PostingsAddress.Value);
            }

            var postings = PostingsReader.Read(addresses);

            Log.DebugFormat("fetched {0} postings lists in {1}", terms.Count, time.Elapsed);

            return postings;
        }

        protected IList<DocumentPosting> GetPostingsList(IList<Term> terms)
        {
            var postings = terms.Count > 0 ? GetManyPostingsLists(terms) : null;

            IList<DocumentPosting> result = new List<DocumentPosting>();

            if (postings != null)
            {
                foreach (var list in postings)
                foreach (var p in list)
                {
                    result.Add(p);
                }
            }

            return result;
        }

        protected IList<DocumentPosting> GetPostingsList(Term term)
        {
            return PostingsReader.Read(new BlockInfo[] { term.Word.PostingsAddress.Value })[0];
        }

        protected IList<DocumentPosting> GetSortedPostingsList(IList<BlockInfo> addresses)
        {
            var result = new List<DocumentPosting>();
            var many = PostingsReader.Read(addresses);

            foreach(var list in many)
            {
                foreach (var posting in list)
                {
                    result.Add(posting);
                }
            }
            result.Sort(new DocumentPostingComparer());
            return result;
        }
    }
}
