using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Serialization.Json {
    public class FileContainer {

        public FileContainer() {

        }
        public FileContainer(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            if (System.IO.File.Exists(filePath)) {
                FileName = System.IO.Path.GetFileName(filePath);
                Data = System.IO.File.ReadAllBytes(filePath);
                CheckLength();
            }
        }
        public FileContainer(string filename, byte[] Data) {
            FileName = filename;
            this.Data = Data;
            CheckLength();
        }
        public void CheckLength() {
            Length = Data != null ? Data.LongLength : 0;
            if (Length == 0) {
                throw new Exception("The specified file is empty.");
            } else if (Length > 2147483647) {
                throw new Exception("The specified file is too large. Maximum size is 2,147,483,647 bytes (2GB).");
            }
        }
        public string FileName { get; set; }
        public long Length { get; set; }
        public byte[] Data { get; set; }
    }
}
