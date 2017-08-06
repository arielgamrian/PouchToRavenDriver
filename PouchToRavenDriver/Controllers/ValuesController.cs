using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using System.IO;
using Microsoft.AspNetCore.Http;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using System.Globalization;
using Newtonsoft.Json.Converters;

namespace PouchToRavenDriver.Controllers
{
    [Route("")]
    public class RootController : Controller
    {
        [HttpGet("")]
        public void Index()
        {
            ValuesController.SetHeaders(this);

            JObject json = new JObject(
                new JProperty("ravendb", "Welcome"),
                new JProperty("version", "2.0.0"),
                new JProperty("vender",
                    new JObject(
                        new JProperty("name", "Hibernating Rhinos"))));

            ValuesController.Write(Response, json);
        }
    }

    [Route("db")]
    public class ValuesController : Controller
    {
        public class RavenDB
        {
            public ConcurrentDictionary<string, ConcurrentDictionary<string, string>> documents;
            private ConcurrentDictionary<string, string> local_documents;
            public string name; // database name.
            public int seq_num; // database update sequence.
            public int last_seq; // last replication sequence
            public string seq_id; // update sequence id.
            public List<JObject> results; // a list of changes made to the database.
            public string session_id;
            public string etag_local;

            public RavenDB(string name)
            {
                documents = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
                local_documents = new ConcurrentDictionary<string, string>();
                this.name = name;
                seq_num = 0;
                last_seq = 0;
                seq_id = Guid.NewGuid().ToString();
                results = new List<JObject>();
                session_id = Guid.NewGuid().ToString();
                etag_local = Guid.NewGuid().ToString();
            }

            // returns the current document.
            public JObject GetCurrentDocument(string id)
            {
                ConcurrentDictionary<string, string> docs;
                documents.TryGetValue(id, out docs);

                JObject winner = null;

                if (docs == null)
                    return null;

                foreach (var doc in docs)
                {
                    JObject jo = JObject.Parse(doc.Value);

                    if (!IsDeleted(jo))
                        winner = GetWinner(jo, winner);
                }

                return winner;
            }

            // returns the document with the higher revision.
            public JObject GetWinner(JObject j1, JObject j2)
            {
                if (j1 == null && j2 == null)
                    return null;

                else if (j2 == null)
                    return j1;

                else if (j1 == null)
                    return j2;

                else if (CompareRevs(j1["_rev"].ToString(), j2["_rev"].ToString()) > 0)
                    return j1;

                return j2;
            }

            // returns visible documents count.
            public int GetDocumentsCount()
            {
                int count = 0;
                foreach (var kvp in documents)
                {
                    if (GetCurrentDocument(kvp.Key) != null)
                        count++;
                }
                return count;
            }

            // returns the document with the specified ID and revision.
            public JObject GetDocument(string id, string rev)
            {
                ConcurrentDictionary<string, string> docs;
                this.documents.TryGetValue(id, out docs);

                string doc;
                docs.TryGetValue(rev, out doc);

                return JObject.Parse(doc);
            }

            // returns the document with the specified ID.
            public string GetLocalDocument(string id)
            {
                string v;
                local_documents.TryGetValue(id, out v);
                return v;
            }

            // insert a local document into the database.
            public void PutLocalDocument(string _document, out int rev)
            {

                JObject document = JObject.Parse(_document);
                string id = document["_id"].ToString();
                bool exists = local_documents.ContainsKey(id);

                if (!exists || document["_rev"] == null)
                    rev = 1;
                else
                    rev = Int32.Parse(db.GetRevId(document["_rev"].ToString())) + 1;

                document["_rev"] = rev;
                local_documents[id] = document.ToString();
            }

            /// <summary>
            /// 
            /// if 'new_edits' is true, assign the document a new rev.
            /// 
            /// if from Raven -> HandleRevs()
            /// if from Pouch -> HandleConflicts()
            /// 
            /// Stores the document.
            /// 
            /// set 'StatusCode' to 201 if the document is new. 200 otherwise.
            /// </summary>
            /// <param name="_document"></param>
            /// <param name="Response"></param>
            /// <param name="new_edits">if 'true', assign a new revision. 
            /// (if the document received from Pouch, Pouch wil add 'new_edits=false' so Raven would know that Pouch already assigned the document a new rev)</param>
            /// <param name="rev">returns the new assigned rev</param>
            public void PutDocument(string _document, HttpResponse Response, bool new_edits, out string rev)
            {
                JObject document = JObject.Parse(_document);
                string id = document["_id"].ToString();
                bool exists = documents.ContainsKey(id);

                if (new_edits)
                {
                    string rev1 = newRev(document["_rev"]);
                    document["_rev"] = rev1;
                }

                JObject loser;
                JObject winner = GetWinner(document, out loser);

                bool hasConflicts = false;

                if (new_edits)
                    HandleRevs(winner);
                else
                    HandleConflicts(winner, loser, out hasConflicts);

                if (exists)
                {
                    if (!hasConflicts)
                    {
                        string x;
                        JObject j = GetCurrentDocument(id);
                        documents[id].TryRemove(j["_rev"].ToString(), out x);
                    }
                    Response.StatusCode = 200;
                }
                else
                {
                    documents[id] = new ConcurrentDictionary<string, string>();
                    Response.StatusCode = 201;
                }

                rev = winner["_rev"].ToString();
                documents[id][rev] = winner.ToString();

                db.addResult(document);
            }

            // returns the document with the higher revision between the document and the current document.
            // loser = the lower revision document.
            private JObject GetWinner(JObject document, out JObject loser)
            {
                string id = document["_id"].ToString();
                bool exists = documents.ContainsKey(id) && GetCurrentDocument(id) != null;

                JObject winner = new JObject();

                if (exists && document["_rev"] != null)
                {
                    JObject current = GetCurrentDocument(id);
                    if (CompareRevs(document["_rev"].ToString(), current["_rev"].ToString()) > 0)
                    {
                        winner = document;
                        if (current["_conflicts"] != null)
                            winner["_conflicts"] = current["_conflicts"];
                        loser = current;
                    }
                    else
                    {
                        winner = current;
                        loser = document;
                    }
                }
                else
                {
                    winner = document;
                    loser = null;
                }
                return winner;
            }

            // returns the higher rev.
            public int CompareRevs(string rev1, string rev2)
            {
                int num1 = GetRevNum(rev1);
                int num2 = GetRevNum(rev2);

                if (num1 == num2)
                {
                    string id1 = GetRevId(rev1);
                    string id2 = GetRevId(rev2);

                    return id1.CompareTo(id2);
                }
                else if (num1 > num2)
                    return 1;
                else
                    return -1;
            }

            // if the document has more than 1 rev, we should create a new child '_revisions' containing an array of all the revs history.
            private void HandleRevs(JObject document)
            {
                string id = document["_id"].ToString();
                bool exists = documents.ContainsKey(id);

                if (!exists)
                    return;

                JObject current = GetCurrentDocument(id);
                string _rev = document["_rev"].ToString();

                if (current["_revisions"] == null)
                {
                    JArray ja = new JArray(GetRevId(current["_rev"].ToString()));

                    document["_revisions"] = new JObject(
                        new JProperty("start", 1),
                        new JProperty("ids", ja));
                }
                else
                    document["_revisions"] = current["_revisions"];

                JArray j = (JArray)document["_revisions"]["ids"];
                j.AddFirst(GetRevId(_rev));
                document["_revisions"]["start"] = j.Count;
            }

            // check for replication conflicts and add them to the document under '_conflicts'.
            private void HandleConflicts(JObject document, JObject conflict, out bool hasConflicts)
            {
                string id = document["_id"].ToString();
                bool exists = documents.ContainsKey(id) && GetCurrentDocument(id) != null;

                hasConflicts = true;

                JObject current = GetCurrentDocument(id);

                if (!exists)
                    return;

                int new_revNum = GetRevNum(document["_rev"].ToString());
                int current_revNum = GetRevNum(conflict["_rev"].ToString());

                foreach (JObject result in results)
                {
                    int seq = GetRevNum(result["seq"].ToString());
                    if (seq > last_seq && result["id"].ToString() == document["_id"].ToString())
                    {
                        if (current["_conflicts"] == null)
                            document["_conflicts"] = new JArray();
                        else
                            document["_conflicts"] = current["_conflicts"];

                        JArray j = (JArray)document["_conflicts"];
                        string rev = conflict["_rev"].ToString();
                        if (j.Count > 0 && CompareRevs(rev, j[0].ToString()) > 0)
                            j.AddFirst(rev);
                        else
                            j.Add(rev);

                        documents[id][rev] = conflict.ToString();
                        hasConflicts = true;

                        return;
                    }
                }
                hasConflicts = false;
            }

            // return whether the document has a child '_deleted=true' so the database know that it should be deleted.
            private bool IsDeleted(JObject document)
            {
                return document["_deleted"] != null && document["_deleted"].ToString().ToLower() == "true";
            }

            // receives the old document rev and returns the next revision.
            // if 'oldRev' is null, return '1-new_rev'.
            // EXAMPLE: receives '1-old_rev' returns '2-'new_rev'
            // 
            public string newRev(JToken oldRev)
            {
                int num = 1;

                if (oldRev != null)
                    num = GetRevNum(oldRev.ToString()) + 1;

                string str = "abcdefghijklmnopqrstuvwxyz1234567890";
                string rev = num + "-";

                Random rnd = new Random();

                for (int i = 0; i < 32; i++)
                    rev += str[rnd.Next(32)];

                return rev;
            }

            // returns the revision number.
            // EXAMPLE: receives '43-rev_id' returns '43'.
            public int GetRevNum(string rev)
            {
                if (rev.IndexOf("-") < 1)
                    return Int32.Parse(rev);

                return Int32.Parse(rev.Substring(0, rev.IndexOf("-")));
            }

            // returns the revision id.
            // EXAMPLE: receives '43-rev_id' returns 'rev_id'.

            public string GetRevId(string rev)
            {
                return rev.Substring(rev.IndexOf("-") + 1);
            }

            // returns whether the document contains the specified revision.
            public bool HasRev(JObject document, string rev)
            {
                rev = GetRevId(rev);

                if (document["_revisions"] != null)
                {
                    foreach (string rv in document["_revisions"]["ids"])
                        if (rev == rv)
                            return true;
                }
                return false;
            }

            // add the new document to the changes list.
            public void addResult(JObject doc)
            {
                seq_num++;
                seq_id = Guid.NewGuid().ToString();
                session_id = Guid.NewGuid().ToString();

                string id = doc["_id"].ToString();
                string rev = doc["_rev"].ToString();

                bool exists = false;

                for (int i = 0; i < results.Count; i++)
                    if (results[i]["id"].ToString() == id)
                    {
                        exists = true;
                        JArray c = (JArray)results[i]["changes"];
                        for (int j = 0; j < c.Count; j++)
                        {
                            string r = c[j]["rev"].ToString();
                            if (HasRev(doc, r))
                                c.RemoveAt(j);
                        }

                        c.AddFirst(new JObject(new JProperty("rev", rev)));

                        if (GetCurrentDocument(id) == null)
                            results[i]["deleted"] = true;
                        else if (results[i]["deleted"] != null)
                            results[i]["deleted"].Remove();

                        results[i]["seq"] = seq_num + "-" + session_id;
                        break;
                    }

                if (!exists)
                {
                    JArray changes = new JArray();

                    changes.Add(new JObject(new JProperty("rev", rev)));

                    JObject result = new JObject(
                   new JProperty("id", id),
                   new JProperty("seq", seq_num + "-" + session_id),
                   new JProperty("changes", changes));

                    if (GetCurrentDocument(id) == null)
                        result.Add("deleted", true);

                    results.Add(result);
                }
            }
        }

        private static RavenDB db;

        // http://docs.couchdb.org/en/2.0.0/api/database/common.html
        [HttpHead("")]
        [HttpGet("")]
        [HttpPut("")]
        [HttpDelete("")]
        [HttpPost("")]
        [HttpOptions("")]
        public void Index()
        {
            SetHeaders(this);

            if (Request == null)
                return;

            JObject message = new JObject();

            switch (Request.Method)
            {

                // Returns the HTTP Headers containing a minimal amount of information about the specified database. Since the response body is empty, using the HEAD method is a lightweight way to check if the database exists already 
                case "HEAD":

                    break;

                // returns information about the specified database.
                case "GET":

                    if (db == null)
                    {
                        message["error"] = "not-found";
                        message["reason"] = "Database does not exist.";
                        Response.StatusCode = 404;

                        Write(Response, message);
                        return;
                    }

                    message["db_name"] = db.name;
                    message["update_seq"] = db.seq_num + "-" + db.seq_id;
                    message["doc_count"] = db.GetDocumentsCount();

                    break;

                // create a new database
                case "PUT":
                    db = new RavenDB("accounts");

                    Response.Headers.Add("Location", "http://localhost:59652/");
                    Response.StatusCode = 201;
                    message["ok"] = "true";

                    break;

                // delete the database
                case "DELETE":
                    if (db == null && Request.Method != "OPTIONS")
                    {
                        message["error"] = "not-found";
                        message["reason"] = "Database does not exist.";
                        Response.StatusCode = 404;
                        Write(Response, message);
                        return;
                    }

                    db.results.Clear();
                    db = null;
                    message["ok"] = "true";

                    break;

                case "OPTIONS":
                    Response.Headers.Add("Access-Control-Max-Age", "600");
                    Response.StatusCode = 204;
                    return;
            }

            Response.Headers.Add("Cache-Control", "must-revalidate");
            Write(Response, message);
        }

        // http://docs.couchdb.org/en/2.0.0/api/document/common.html
        [HttpGet("{id}")]
        [HttpPost("{id}")]
        [HttpPut("{id}")]
        [HttpDelete("{id}")]
        [HttpOptions("{id}")]
        public void Docs(string id)
        {
            JObject message = new JObject();

            SetHeaders(this);

            if (db == null && Request.Method != "OPTIONS")
            {
                message["error"] = "not-found";
                message["reason"] = "Database does not exist.";
                Response.StatusCode = 404;
                Write(Response, message);
                return;
            }

            if (Request == null)
                return;

            switch (Request.Method)
            {

                // returns the current document with the specified ID.
                case "GET":

                    message = db.GetCurrentDocument(id);

                    if (message == null)
                    {
                        message = new JObject(
                        new JProperty("error", "id not found"));
                    }
                    break;

                // insert a new document with the specified to the database.
                case "PUT":

                    string _document;
                    using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        _document = reader.ReadToEnd();
                    }

                    string rev;
                    db.PutDocument(_document, Response, true, out rev);

                    message = new JObject(
                        new JProperty("id", id),
                        new JProperty("ok", "true"),
                        new JProperty("rev", rev));

                    break;

                case "OPTIONS":
                    Response.StatusCode = 204;
                    return;
            }

            Write(Response, message);
        }

        // http://docs.couchdb.org/en/2.0.0/api/database/changes.html?highlight=_changes
        [HttpGet("_changes")]
        public void _changes(string since)
        {
            SetHeaders(this);

            if (Request == null)
                return;

            JObject message = new JObject();
            switch (Request.Method)
            {
                // return the changes made to the database since the specified sequence.
                case "GET":

                    string session = db.seq_num + "-" + db.session_id;

                    int snce = 0;
                    if (since != "0")
                        snce = db.GetRevNum(since);

                    JArray results = new JArray();

                    foreach (JObject jo in db.results)
                        if (snce < db.GetRevNum(jo["seq"].ToString()))
                            results.Add(jo);

                    message["results"] = results;
                    message["last_seq"] = session;
                    message["pending"] = 0;
                    break;
            }



            Response.Headers.Add("Cache-Control", "must-revalidate");
            Write(Response, message);
        }

        // http://docs.couchdb.org/en/2.0.0/api/database/bulk-api.html
        [HttpGet("_all_docs")]
        [HttpPost("_all_docs")]
        public void _all_docs()
        {
            JObject message = new JObject();

            if (db == null && Request.Method != "OPTIONS")
            {
                message["error"] = "not-found";
                message["reason"] = "Database does not exist.";
                Response.StatusCode = 404;
                Write(Response, message);
                return;
            }

            SetHeaders(this);

            if (Request == null)
                return;

            switch (Request.Method)
            {

                // return a list of all documents
                case "GET":

                    message = new JObject(new JProperty("total_rows", db.GetDocumentsCount()));

                    JArray rows = new JArray();

                    foreach (var kvp in db.documents)
                    {

                        JObject document = db.GetCurrentDocument(kvp.Key);
                        if (document == null)
                            continue;

                        JObject data = new JObject(
                                new JProperty("id", kvp.Key),
                                new JProperty("key", kvp.Key),
                                new JProperty("doc", document),
                                new JProperty("value", new JArray(new JObject(
                                    new JProperty("rev", document["_rev"].ToString())))));

                        rows.Add(new JObject(data));
                    }

                    message.Add("rows", rows);


                    break;

                // recives an array of keys and return all the document with the specified keys (ID);
                case "POST":

                    string input = "";

                    using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        input = reader.ReadToEnd();
                    }

                    message = new JObject(
                        new JProperty("total_rows", db.GetDocumentsCount()));

                    JArray rows1 = new JArray();

                    if (String.IsNullOrEmpty(input))
                        return;

                    JObject j = JObject.Parse(input);

                    foreach (string key in j["keys"])
                    {
                        JObject document = db.GetCurrentDocument(key);

                        JObject data = new JObject(
                            new JProperty("id", key),
                            new JProperty("key", key),
                            new JProperty("doc", document),
                            new JProperty("value", new JArray(new JObject(
                                new JProperty("rev", document["_rev"].ToString())))));

                        rows1.Add(new JObject(data));
                    }

                    message.Add("rows", rows1);

                    break;

                case "OPTIONS":
                    Response.StatusCode = 204;
                    return;
            }
            Response.Headers.Add("Cache-Control", "must-revalidate");
            Write(Response, message);
        }

        // https://github.com/couchbase/sync_gateway/wiki/Bulk-GET
        [HttpPost("_bulk_get")]
        [HttpOptions("_bulk_get")]
        public void _bulk_get()
        {
            SetHeaders(this);

            JObject message = new JObject();

            if (db == null && Request.Method != "OPTIONS")
            {
                message["error"] = "not-found";
                message["reason"] = "Database does not exist.";
                Response.StatusCode = 404;
                Write(Response, message);
                return;
            }

            if (Request == null)
                return;


            switch (Request.Method)
            {
                // receive an array of objects containing id and revision and return a list of documents with the specified revisions and ids.
                case "POST":

                    string input = "";

                    using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        input = reader.ReadToEnd();
                    }

                    if (String.IsNullOrEmpty(input))
                        return;

                    JObject j = JObject.Parse(input);
                    JArray arr = new JArray();
                    JObject results = new JObject();

                    foreach (JObject jo in j["docs"])
                    {
                        JObject document = db.GetDocument(jo["id"].ToString(), jo["rev"].ToString());

                        JArray docs = new JArray(new JObject(new JProperty("ok", document)));
                        results["docs"] = docs;
                        results["id"] = jo["id"];

                        arr.Add(new JObject(results));
                    }

                    message.Add("results", arr);

                    break;

                case "OPTIONS":
                    Response.StatusCode = 204;
                    return;
            }
            Response.Headers.Add("Cache-Control", "must-revalidate");
            Write(Response, message);
        }

        // https://wiki.apache.org/couchdb/HttpPostRevsDiff
        [HttpPost("_revs_diff")]
        [HttpOptions("_revs_diff")]
        public void _revs_diff()
        {
            SetHeaders(this);

            if (Request == null)
                return;

            JObject message = new JObject();

            switch (Request.Method)
            {
                // receive an array of ids and revisions and return the revisions which are not in the database.
                case "POST":
                    Response.Headers.Add("Cache-Control", "must-revalidate");

                    string input = "";

                    using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        input = reader.ReadToEnd();
                    }

                    if (String.IsNullOrEmpty(input))
                        return;

                    JObject json = JObject.Parse(input);

                    foreach (var x in json)
                    {
                        JArray revs = new JArray();
                        if (!db.documents.ContainsKey(x.Key))
                        {
                            revs = (JArray)x.Value;
                        }
                        else
                        {
                            JArray y = JArray.Parse(x.Value.ToString());
                            foreach (string rev in y)
                            {
                                if (!db.documents[x.Key].ContainsKey(rev))
                                    revs.Add(rev);
                            }
                        }
                        if (revs.Count > 0)
                            message[x.Key] = new JObject(new JProperty("missing", revs));
                    }
                    break;

                case "OPTIONS":
                    Response.Headers.Add("Access-Control-Max-Age", "600");
                    Response.StatusCode = 204;
                    return;
            }
            Write(Response, message);
        }

        // http://docs.couchdb.org/en/2.0.0/api/database/bulk-api.html#db-bulk-docs
        [HttpPost("_bulk_docs")]
        [HttpOptions("_bulk_docs")]
        public void _bulk_docs()
        {
            SetHeaders(this);

            if (Request == null)
                return;

            switch (Request.Method)
            {
                // receive a list of document and store them in the database.
                case "POST":
                    Response.Headers.Add("Cache-Control", "must-revalidate");

                    string input = "";

                    using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        input = reader.ReadToEnd();
                    }

                    JObject docs = JObject.Parse(input);


                    bool new_edits = true;

                    if (docs["new_edits"] != null && docs["new_edits"].ToString() == "False")
                        new_edits = false;

                    JArray message = new JArray();

                    foreach (var x in docs)
                    {
                        if (x.Key == "new_edits")
                            continue;
                        foreach (var y in x.Value)
                        {
                            JObject document = JObject.Parse(y.ToString());
                            string rev = "";
                            db.PutDocument(document.ToString(), Response, new_edits, out rev);
                            if (new_edits)
                                message.Add(new JObject(
                                    new JProperty("id", document["_id"]),
                                    new JProperty("ok", "True"),
                                    new JProperty("rev", rev)));
                        }
                    }

                    Response.StatusCode = 201;

                    Write(Response, message);
                    break;

                case "OPTIONS":
                    Response.Headers.Add("Access-Control-Max-Age", "600");
                    Response.StatusCode = 204;
                    return;
            }
        }

        // http://docs.couchdb.org/en/2.0.0/api/local.html?highlight=_local
        // The Local (non-replicating) document interface allows you to create local documents that are not replicated to other databases. 
        // These documents can be used to hold configuration or other information that is required specifically on the local CouchDB instance.
        // NOTE: Don't take my example of the 'db/_local' implementation - The '_local' supposed to be exactly the same as db/{id}, except that the document is not replicated.
        [HttpGet("_local/{id}")]
        [HttpPost("_local/{id}")]
        [HttpPut("_local/{id}")]
        [HttpOptions("_local/{id}")]
        public void _local(string id)
        {
            SetHeaders(this);

            if (Request == null)
                return;

            JObject message = new JObject();
            string _history;

            switch (Request.Method)
            {

                // returns the specified local document.
                case "GET":
                    Response.Headers.Add("Cache-Control", "must-revalidate");

                    string ifnonematch = Request.Headers["if-none-match"];

                    string etag = db.etag_local;

                    if (!String.IsNullOrEmpty(etag) && !String.IsNullOrEmpty(ifnonematch) && ifnonematch == etag)
                    {
                        Response.Headers.Add("ETag", etag);
                        Response.StatusCode = 304;
                        return;
                    }

                    _history = db.GetLocalDocument("_local/" + id);

                    if (String.IsNullOrEmpty(_history))
                    {
                        message = new JObject(
                            new JProperty("error", "not_found"),
                            new JProperty("reason", "missing"));
                        Response.StatusCode = 404;
                        Write(Response, message);
                        return;
                    }
                    else
                        message = JObject.Parse(_history);

                    break;

                // Stores the specified local document.
                case "PUT":
                    Response.Headers.Add("Cache-Control", "must-revalidate");
                    Response.Headers.Add("Location", "http://localhost:59652/db/_local/" + id);

                    Response.StatusCode = 201;

                    using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        _history = reader.ReadToEnd();
                    }

                    JObject history = JObject.Parse(_history);

                    db.last_seq = db.GetRevNum(history["last_seq"].ToString());

                    int rev;

                    db.PutLocalDocument(_history, out rev);

                    message = new JObject(
                        new JProperty("id", "_local/" + id),
                        new JProperty("ok", "True"),
                        new JProperty("rev", "0-" + rev));

                    etag = "\"" + Guid.NewGuid().ToString() + "\"";
                    db.etag_local = etag;
                    Response.Headers.Add("ETag", etag);

                    break;

                case "OPTIONS":
                    Response.StatusCode = 204;
                    Response.Headers.Add("Access-Control-Max-Age", "600");
                    return;

            }
            Write(Response, message);
        }

        // write a response containing the JObject.
        public static void Write(HttpResponse r, JObject json)
        {
            using (var writer = new StreamWriter(r.Body))
            {
                writer.Write(json);
            }
        }

        // write a response containing the JArray.
        public static void Write(HttpResponse r, JArray json)
        {
            using (var writer = new StreamWriter(r.Body))
            {
                writer.Write(json);
            }
        }

        // write a response containing the message.
        public static void Write(HttpResponse r, string message)
        {
            JObject json = JObject.Parse(message);

            using (var writer = new StreamWriter(r.Body))
            {
                writer.Write(json);
            }
        }

        public static void SetHeaders(ControllerBase c)
        {
            c.Response.ContentType = "application/json";
            string origin = c.Request.Headers["Origin"];
            c.Response.Headers.Add("Access-Control-Allow-Origin", "http://localhost:8000");
            c.Response.Headers.Add("Access-Control-Allow-Credentials", "true");
            c.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            c.Response.Headers.Add("Access-Control-Expose-Headers", "content-type, cache-control, accept-ranges, etag, server, x-couch-request-id, x-couch-update-newrev, x-couchdb-body-time");
            c.Response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS, PUT, DELETE, COPY");
        }
    }
}
