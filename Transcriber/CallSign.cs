using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Transcriber {
    public class Callsign {
        public string message { get; set; }
        public string Prefix { get; set; }           // "K", "G", "JA"
        public string Suffix { get; set; }           // "1ABC"
        public string FullPrefix => GetFullPrefix(); // "K1"
        public string Country { get; set; }          // "United States"
        public string DXCCEntity { get; set; }       // "291" for USA
        public string Region { get; set; }           // "W1 - New England"
        public string LicenseClass { get; set; }     // "Technician", "General", "Extra"
        public string OperatorName { get; set; }
        public string GridSquare { get; set; }       // "FN31"
        public string CQZone { get; set; }           // "5"
        public string ITUZone { get; set; }          // "8"
        public double? Latitude { get; set; }        // Optional GPS lat/lon
        public double? Longitude { get; set; }
        public bool IsValid { get; set; } = false; // Indicates if the callsign is valid

        public Callsign(string text) {
            text = text.ToUpper();
            ParseCallsign();
        }

        public Callsign() {

        }

        Dictionary<string, string> correctionMaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
    {"kodak", "q"}, {"kordak", "q"}, {"alpha", "a"}, {"bravo", "b"},
    {"charlie", "c"}, {"delta", "d"}, {"echo", "e"}, {"foxtrot", "f"},
    {"golf", "g"}, {"hotel", "h"}, {"india", "i"}, {"juliet", "j"},
    {"kilo", "k"}, {"lima", "l"}, {"mike", "m"}, {"november", "n"},
    {"oscar", "o"}, {"papa", "p"}, {"quebec", "q"}, {"romeo", "r"},
    {"sierra", "s"}, {"tango", "t"}, {"uniform", "u"}, {"victor", "v"},
    {"whiskey", "w"}, {"x-ray", "x"}, {"xray", "x"}, {"yankee", "y"},
    {"zulu", "z"}, {"king", "k"}, {"charles", "c"}, {"one", "1"},
    {"two", "2"}, {"three", "3"}, {"four", "4"}, {"five", "5"},
    {"six", "6"}, {"seven", "7"}, {"eight", "8"}, {"nine", "9"},
    {"zero", "0"}, {"box", "b"}, {"key local", "k"}, {"niner", "9"},
    {"kilowatt", "k"}, {"friends", "f"}, {"cielo", "k"}, {"celo", "k"},
    {"quadec", "q"}, {"hilo", "k"}, {"hordak", "q"}, {"fox", "f"},
    {"zed", "z"}, {"hopper", "p"}
};

        public string Serialize() {
            return Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
        }
        private void CorrectPhonetics() {
            // Sort longer keys first to match multi-word phrases
            var sortedKeys = correctionMaps.Keys.OrderByDescending(k => k.Split(' ').Length);

            // Tokenize input
            var words = Regex.Split(message, @"(\s+)"); // preserve whitespace
    
            var output = new List<string>();
            int i = 0;

            while (i < words.Length) {
                bool matched = false;

                foreach (var key in sortedKeys) {
                    var keyParts = key.Split(' ');
                    if (i + keyParts.Length * 2 - 1 >= words.Length)
                        continue;

                    bool isMatch = true;
                    for (int j = 0; j < keyParts.Length; j++) {
                        if (!string.Equals(words[i + j * 2], keyParts[j], StringComparison.OrdinalIgnoreCase)) {
                            isMatch = false;
                            break;
                        }
                    }

                    if (isMatch) {
                        output.Add(correctionMaps[key]); // add substituted character
                        i += keyParts.Length * 2 - 1; // skip over words and spaces
                        matched = true;
                        break;
                    }
                }

                if (!matched) {
                    output.Add(words[i]);
                }

                i++;
            }

            string result = string.Join("", output);
            message = result;
        }

        public void ParseCallsign() {
            //var regex = new System.Text.RegularExpressions.Regex(@"\b[A-KM-NP-Z]{1,2}[0-9][A-Z]{1,3}\b", RegexOptions.IgnoreCase);
            var regex = new System.Text.RegularExpressions.Regex(@"\b([A-Z0-9]{1,2}[0-9][A-Z]{1,4})\b", RegexOptions.IgnoreCase);
            //\b([A-Z0-9]{1,2}[0-9][A-Z]{1,4})\b
            CorrectPhonetics();
            var val = regex.Match(message).Value.ToUpper();
            message = message.Replace(val, string.Empty);
            // Very basic parsing: extract prefix and suffix
            // Real-world parsing would require referencing ITU prefix tables and call sign rules

            // Example: K1ABC
            IsValid = true;
            var prefixEnd = 0;
            for (int i = 0; i < val.Length; i++) {
                if (char.IsDigit(val[i])) {
                    prefixEnd = i + 1;
                    break;
                }
            }

            Prefix = val.Substring(0, prefixEnd);   // "K1"
            Suffix = val.Substring(prefixEnd);      // "ABC"

            // Example hardcoded mappings (replace with real data sources or lookups)
            ResolvePrefix(Prefix);
        }

        private void ResolvePrefix(string prefix) {
            // Map prefix to region (simplified)
            if (string.IsNullOrEmpty(prefix)) {
                Country = "Unknown";
                DXCCEntity = "Unknown";
                Region = "Unknown";
                CQZone = "Unknown";
                ITUZone = "Unknown";
                IsValid = false;
                return;
            }
            switch (prefix) {
                // United States and territories
                case string p when p.StartsWith("K") || p.StartsWith("W") || p.StartsWith("N"):
                    Country = "United States";
                    DXCCEntity = "291";
                    Region = GetUSRegion(p);
                    CQZone = "5";
                    ITUZone = "8";
                    break;
                case string p when p.StartsWith("KH6") || p.StartsWith("WH6") || p.StartsWith("AH6") || p.StartsWith("NH6"):
                    Country = "Hawaii";
                    DXCCEntity = "110";
                    CQZone = "31";
                    ITUZone = "61";
                    break;
                case string p when p.StartsWith("KL7") || p.StartsWith("WL7") || p.StartsWith("NL7") || p.StartsWith("AL7"):
                    Country = "Alaska";
                    DXCCEntity = "6";
                    CQZone = "1";
                    ITUZone = "1";
                    break;
                case string p when p.StartsWith("KP4"):
                    Country = "Puerto Rico";
                    DXCCEntity = "202";
                    CQZone = "8";
                    ITUZone = "11";
                    break;
                case string p when p.StartsWith("KP2"):
                    Country = "US Virgin Islands";
                    DXCCEntity = "285";
                    CQZone = "8";
                    ITUZone = "11";
                    break;
                // Canada
                case string p when p.StartsWith("VO") || p.StartsWith("VY") || p.StartsWith("VA") || p.StartsWith("VE"):
                    Country = "Canada";
                    DXCCEntity = "1";
                    CQZone = "4";
                    ITUZone = "9";
                    break;

                // Japan
                case string p when p.StartsWith("JA") || p.StartsWith("JE") || p.StartsWith("JF") || p.StartsWith("JG") || p.StartsWith("JI") || p.StartsWith("JJ") || p.StartsWith("JK") || p.StartsWith("JL") || p.StartsWith("JM") || p.StartsWith("JN") || p.StartsWith("JO"):
                    Country = "Japan";
                    DXCCEntity = "339";
                    CQZone = "25";
                    ITUZone = "45";
                    break;
                // United Kingdom
                case string p when p.StartsWith("G") || p.StartsWith("M") || p.StartsWith("2E"):
                    Country = "England";
                    DXCCEntity = "223";
                    CQZone = "14";
                    ITUZone = "27";
                    break;
                case string p when p.StartsWith("GM") || p.StartsWith("MM"):
                    Country = "Scotland";
                    DXCCEntity = "279";
                    CQZone = "14";
                    ITUZone = "27";
                    break;
                case string p when p.StartsWith("GW") || p.StartsWith("MW"):
                    Country = "Wales";
                    DXCCEntity = "294";
                    CQZone = "14";
                    ITUZone = "27";
                    break;
                case string p when p.StartsWith("GI") || p.StartsWith("MI"):
                    Country = "Northern Ireland";
                    DXCCEntity = "265";
                    CQZone = "14";
                    ITUZone = "27";
                    break;

                // Germany
                case string p when p.StartsWith("DL") || p.StartsWith("DA") || p.StartsWith("DB") || p.StartsWith("DC"):
                    Country = "Germany";
                    DXCCEntity = "230";
                    CQZone = "14";
                    ITUZone = "28";
                    break;

                // Australia
                case string p when p.StartsWith("VK"):
                    Country = "Australia";
                    DXCCEntity = "150";
                    CQZone = "30";
                    ITUZone = "59";
                    break;

                // France
                case string p when p.StartsWith("F"):
                    Country = "France";
                    DXCCEntity = "227";
                    CQZone = "14";
                    ITUZone = "27";
                    break;

                // Italy
                case string p when p.StartsWith("I") && !p.StartsWith("IS"):
                    Country = "Italy";
                    DXCCEntity = "248";
                    CQZone = "15";
                    ITUZone = "28";
                    break;

                // Spain
                case string p when p.StartsWith("EA") || p.StartsWith("EB") || p.StartsWith("EC"):
                    Country = "Spain";
                    DXCCEntity = "281";
                    CQZone = "14";
                    ITUZone = "37";
                    break;

                // Russia
                case string p when p.StartsWith("RA") || p.StartsWith("RK") || p.StartsWith("RV") || p.StartsWith("UA"):
                    Country = "Russia";
                    DXCCEntity = "222";
                    CQZone = "16";
                    ITUZone = "29";
                    break;

                // China
                case string p when p.StartsWith("BY") || p.StartsWith("BA") || p.StartsWith("BG"):
                    Country = "China";
                    DXCCEntity = "318";
                    CQZone = "24";
                    ITUZone = "43";
                    break;

                // Brazil
                case string p when p.StartsWith("PY") || p.StartsWith("PP") || p.StartsWith("PT"):
                    Country = "Brazil";
                    DXCCEntity = "108";
                    CQZone = "11";
                    ITUZone = "12";
                    break;

                // Argentina
                case string p when p.StartsWith("LU") || p.StartsWith("LW"):
                    Country = "Argentina";
                    DXCCEntity = "100";
                    CQZone = "13";
                    ITUZone = "14";
                    break;

                // South Africa
                case string p when p.StartsWith("ZS"):
                    Country = "South Africa";
                    DXCCEntity = "462";
                    CQZone = "38";
                    ITUZone = "57";
                    break;

                // New Zealand
                case string p when p.StartsWith("ZL"):
                    Country = "New Zealand";
                    DXCCEntity = "170";
                    CQZone = "32";
                    ITUZone = "60";
                    break;
                // ITU series blocks (selected examples)
                case string p when p.StartsWith("AAA") || p.StartsWith("AAB") /*…*/ || p.StartsWith("ALZ"):
                    Country = "United States";
                    DXCCEntity = "291";
                    CQZone = "5"; ITUZone = "8";
                    break;
                case string p when p.StartsWith("AMA") || p.StartsWith("AMZ"):
                    Country = "Spain";
                    DXCCEntity = "224";
                    CQZone = "14"; ITUZone = "27";
                    break;
                case string p when p.StartsWith("ATA") || p.StartsWith("AWZ"):
                    Country = "India";
                    DXCCEntity = "126";
                    CQZone = "22"; ITUZone = "41";
                    break;
                case string p when p.StartsWith("AXA") || p.StartsWith("AXZ"):
                    Country = "Australia";
                    DXCCEntity = "150";
                    CQZone = "30"; ITUZone = "59";
                    break;
                case string p when p.StartsWith("AYA") || p.StartsWith("AZZ"):
                    Country = "Argentina";
                    DXCCEntity = "10";
                    CQZone = "13"; ITUZone = "14";
                    break;
                case string p when p.StartsWith("A2A") || p.StartsWith("A2Z"):
                    Country = "Botswana";
                    DXCCEntity = "1007";
                    CQZone = "38"; ITUZone = "57";
                    break;
                case string p when p.StartsWith("A4A") || p.StartsWith("A4Z"):
                    Country = "Oman";
                    DXCCEntity = "12";
                    CQZone = "21"; ITUZone = "39";
                    break;
                case string p when p.StartsWith("BAA") || p.StartsWith("BZZ"):
                    Country = "China";
                    DXCCEntity = "30";
                    CQZone = "24"; ITUZone = "43";
                    break;
                case string p when p.StartsWith("CFA") || p.StartsWith("CKZ"):
                    Country = "Canada";
                    DXCCEntity = "1";
                    CQZone = "4"; ITUZone = "9";
                    break;
                case string p when p.StartsWith("CAA") || p.StartsWith("CEZ"):
                    Country = "Chile";
                    DXCCEntity = "111";
                    CQZone = "12"; ITUZone = "19";
                    break;
                case string p when p.StartsWith("CYA") || p.StartsWith("CZZ"):
                    Country = "Canada";
                    DXCCEntity = "1";
                    CQZone = "4"; ITUZone = "9";
                    break;
                case string p when p.StartsWith("DL") || p.StartsWith("DR"):
                    Country = "Germany";
                    DXCCEntity = "230";
                    CQZone = "14"; ITUZone = "28";
                    break;
                case string p when p.StartsWith("EKA") || p.StartsWith("EKZ"):
                    Country = "Armenia";
                    DXCCEntity = "247";
                    CQZone = "18"; ITUZone = "29";
                    break;
                case string p when p.StartsWith("E4A") || p.StartsWith("E4Z"):
                    Country = "Palestinian Authority";
                    DXCCEntity = "642";
                    CQZone = "20"; ITUZone = "38";
                    break;
                case string p when p.StartsWith("E5A") || p.StartsWith("E5Z"):
                    Country = "Cook Islands";
                    DXCCEntity = "252";
                    CQZone = "32"; ITUZone = "60";
                    break;
                case string p when p.StartsWith("E7A") || p.StartsWith("E7Z"):
                    Country = "Bosnia & Herzegovina";
                    DXCCEntity = "60";
                    CQZone = "15"; ITUZone = "28";
                    break;
                case string p when p.StartsWith("FAA") || p.StartsWith("FZZ"):
                    Country = "France";
                    DXCCEntity = "227";
                    CQZone = "14"; ITUZone = "27";
                    break;
                case string p when p.StartsWith("GAA") || p.StartsWith("GZZ"):
                    Country = "United Kingdom";
                    DXCCEntity = "223";
                    CQZone = "14"; ITUZone = "27";
                    break;
                case string p when p.StartsWith("HCA") || p.StartsWith("HDZ"):
                    Country = "Ecuador";
                    DXCCEntity = "44";
                    CQZone = "10"; ITUZone = "18";
                    break;
                case string p when p.StartsWith("LA") || p.StartsWith("LN"):
                    Country = "Norway";
                    DXCCEntity = "40";
                    CQZone = "17"; ITUZone = "28";
                    break;
                case string p when p.StartsWith("PA") || p.StartsWith("PI"):
                    Country = "Netherlands";
                    DXCCEntity = "150";
                    CQZone = "14"; ITUZone = "27";
                    break;
                case string p when p.StartsWith("RA") || p.StartsWith("RZ"):
                    Country = "Russia";
                    DXCCEntity = "222";
                    CQZone = "16"; ITUZone = "29";
                    break;
                case string p when p.StartsWith("UA") || p.StartsWith("UI"):
                    Country = "Russia";
                    DXCCEntity = "222";
                    CQZone = "16"; ITUZone = "29";
                    break;
                case string p when p.StartsWith("VK") || p.StartsWith("VH") || p.StartsWith("VN"):
                    Country = "Australia";
                    DXCCEntity = "150";
                    CQZone = "30"; ITUZone = "59";
                    break;
                case string p when p.StartsWith("XE") || p.StartsWith("XI"):
                    Country = "Mexico";
                    DXCCEntity = "107";
                    CQZone = "8"; ITUZone = "10";
                    break;
                // Default fallback
                default:
                    Country = "Unknown";
                    DXCCEntity = "0";
                    Region = "Unknown";
                    CQZone = "Unknown";
                    ITUZone = "Unknown";
                    IsValid = false;
                    break;
            }
        }

        private string GetFullPrefix() {
            return Prefix;
        }

        private string GetUSRegion(string prefix) {
            // Extract number from prefix and assign region
            // Example: W1 => New England, K4 => Southeast, etc.
            if (prefix.Length >= 2 && char.IsDigit(prefix[1])) {
                switch (prefix[1]) {
                    case '0': return "W0 - Midwest";
                    case '1': return "W1 - New England";
                    case '2': return "W2 - Mid-Atlantic";
                    case '3': return "W3 - Mid-Atlantic";
                    case '4': return "W4 - Southeast";
                    case '5': return "W5 - South Central";
                    case '6': return "W6 - California";
                    case '7': return "W7 - Northwest";
                    case '8': return "W8 - Great Lakes";
                    case '9': return "W9 - Central";
                }
            }
            return "Unknown";
        }

        public override string ToString() {
            return $"{message} ({Country}, {Region}, Grid: {GridSquare})";
        }
    }
}
