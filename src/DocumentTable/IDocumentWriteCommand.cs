﻿namespace DocumentTable
{
    public interface IDocumentWriteCommand
    {
        void Write(Document document, IWriteSession session);
    }
}