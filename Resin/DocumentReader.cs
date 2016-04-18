using System.Collections.Generic;
using System.IO;
using Resin.IO;

namespace Resin
{
    public class DocumentReader
    {
        protected readonly string Directory;
        protected readonly Dictionary<string, DocFile> DocFiles;
        protected readonly Dictionary<string, Document> Docs; 
        protected DixFile Dix;

        public DocumentReader(string directory, DixFile dix)
        {
            Directory = directory;
            Dix = dix;
            DocFiles = new Dictionary<string, DocFile>();
            Docs = new Dictionary<string, Document>();
        }

        public IDictionary<string, string> GetDoc(string docId)
        {
            Document doc;
            if (!Docs.TryGetValue(docId, out doc))
            {
                var file = GetDocFile(docId);
                doc = file.Docs[docId];                
            }
            return doc.Fields;
        }

        protected DocFile GetDocFile(string docId)
        {
            var fileId = Dix.DocIdToFileId[docId];
            var fileName = Path.Combine(Directory, fileId + ".d");
            DocFile file;
            if (!DocFiles.TryGetValue(fileId, out file))
            {
                file = DocFile.Load(fileName);
                DocFiles[fileId] = file;
            }
            return file;
        }
    }
}