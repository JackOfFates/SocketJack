using System;
using System.Collections.Generic;
using System.Text;

namespace HamTranscriber {
    internal class Program {
        static void Main(string[] args) {
            var filename = args.Length > 0 ? args[0] : string.Empty;
            if (string.IsNullOrEmpty(filename)) {
                Console.WriteLine("Please provide a filename as an argument.");
                Environment.Exit(0);
                return;
            } else {
                try {
                    var transcriber = new Transcriber();
                    Console.WriteLine(transcriber.FromFile(filename).Result.Serialize());
                } catch (Exception ex) {
                    Console.WriteLine($"An error occurred: {ex.Message}");
                }
                Environment.Exit(0);
            }
        }
    }
}
