using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SocketJack.Net.Database {

    /// <summary>
    /// Generates Entity Framework DbContext and entity classes from a SQL Server database schema.
    /// Supports both C# and VB.NET code generation with automatic build integration.
    /// </summary>
    public class DbContextGenerator {

        #region Properties

        /// <summary>
        /// The database connection string.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// The namespace for generated classes.
        /// </summary>
        public string Namespace { get; set; } = "Generated";

        /// <summary>
        /// The name of the DbContext class.
        /// </summary>
        public string ContextName { get; set; } = "AppDbContext";

        /// <summary>
        /// Output directory for generated files.
        /// </summary>
        public string OutputDirectory { get; set; }

        /// <summary>
        /// Target language (CSharp or VisualBasic).
        /// </summary>
        public CodeLanguage Language { get; set; } = CodeLanguage.CSharp;

        /// <summary>
        /// Whether to generate partial classes.
        /// </summary>
        public bool GeneratePartialClasses { get; set; } = true;

        /// <summary>
        /// Whether to include navigation properties.
        /// </summary>
        public bool IncludeNavigationProperties { get; set; } = true;

        /// <summary>
        /// Tables to exclude from generation.
        /// </summary>
        public HashSet<string> ExcludedTables { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Event raised during code generation for progress reporting.
        /// </summary>
        public event EventHandler<GeneratorProgressEventArgs> Progress;

        #endregion

        #region Schema Classes

        public class TableSchema {
            public string Name { get; set; }
            public string Schema { get; set; } = "dbo";
            public List<ColumnSchema> Columns { get; set; } = new List<ColumnSchema>();
            public List<string> PrimaryKeys { get; set; } = new List<string>();
            public List<ForeignKeySchema> ForeignKeys { get; set; } = new List<ForeignKeySchema>();
        }

        public class ColumnSchema {
            public string Name { get; set; }
            public string SqlType { get; set; }
            public bool IsNullable { get; set; }
            public bool IsIdentity { get; set; }
            public bool IsComputed { get; set; }
            public int? MaxLength { get; set; }
            public int? Precision { get; set; }
            public int? Scale { get; set; }
            public string DefaultValue { get; set; }
            public bool IsPrimaryKey { get; set; }
            public bool IsRowVersion { get; set; }
        }

        public class ForeignKeySchema {
            public string Name { get; set; }
            public string ColumnName { get; set; }
            public string ReferencedTable { get; set; }
            public string ReferencedColumn { get; set; }
        }

        #endregion

        #region Constructors

        public DbContextGenerator() { }

        public DbContextGenerator(string connectionString) {
            ConnectionString = connectionString;
        }

        public DbContextGenerator(string connectionString, string outputDirectory, CodeLanguage language = CodeLanguage.CSharp) {
            ConnectionString = connectionString;
            OutputDirectory = outputDirectory;
            Language = language;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Detects the project language from the project file.
        /// </summary>
        public static CodeLanguage DetectLanguage(string projectPath) {
            if (string.IsNullOrEmpty(projectPath))
                return CodeLanguage.CSharp;

            var ext = Path.GetExtension(projectPath).ToLowerInvariant();
            return ext switch {
                ".vbproj" => CodeLanguage.VisualBasic,
                ".csproj" => CodeLanguage.CSharp,
                _ => CodeLanguage.CSharp
            };
        }

        /// <summary>
        /// Detects the project language from existing source files in a directory.
        /// </summary>
        public static CodeLanguage DetectLanguageFromDirectory(string directory) {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return CodeLanguage.CSharp;

            var vbFiles = Directory.GetFiles(directory, "*.vb", SearchOption.TopDirectoryOnly).Length;
            var csFiles = Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly).Length;

            return vbFiles > csFiles ? CodeLanguage.VisualBasic : CodeLanguage.CSharp;
        }

        /// <summary>
        /// Generates DbContext and entity classes from the database schema.
        /// </summary>
        public GenerationResult Generate() {
            var result = new GenerationResult();

            try {
                ReportProgress("Connecting to database...");
                var tables = GetDatabaseSchema();

                ReportProgress($"Found {tables.Count} tables");

                // Filter excluded tables
                tables = tables.Where(t => !ExcludedTables.Contains(t.Name)).ToList();

                // Generate entity classes
                foreach (var table in tables) {
                    ReportProgress($"Generating entity: {table.Name}");
                    var entityCode = GenerateEntityClass(table);
                    var fileName = ToPascalCase(table.Name) + GetFileExtension();
                    result.GeneratedFiles.Add(fileName, entityCode);
                }

                // Generate DbContext
                ReportProgress($"Generating DbContext: {ContextName}");
                var contextCode = GenerateDbContextClass(tables);
                var contextFileName = ContextName + GetFileExtension();
                result.GeneratedFiles.Add(contextFileName, contextCode);

                // Write files if output directory specified
                if (!string.IsNullOrEmpty(OutputDirectory)) {
                    Directory.CreateDirectory(OutputDirectory);
                    foreach (var file in result.GeneratedFiles) {
                        var filePath = Path.Combine(OutputDirectory, file.Key);
                        File.WriteAllText(filePath, file.Value, Encoding.UTF8);
                        result.WrittenFiles.Add(filePath);
                    }
                }

                result.Success = true;
                ReportProgress("Generation complete");
            } catch (Exception ex) {
                result.Success = false;
                result.Error = ex.Message;
                result.Exception = ex;
            }

            return result;
        }

        /// <summary>
        /// Generates DbContext and entity classes asynchronously.
        /// </summary>
        public Task<GenerationResult> GenerateAsync() {
            return Task.Run(() => Generate());
        }

        /// <summary>
        /// Gets the database schema using DataClient.
        /// </summary>
        public List<TableSchema> GetDatabaseSchema() {
            var tables = new List<TableSchema>();

            using (var client = new DataClient(ConnectionString)) {
                client.Open();

                // Get tables
                var tablesResult = client.ExecuteQuery(@"
                    SELECT TABLE_SCHEMA, TABLE_NAME 
                    FROM INFORMATION_SCHEMA.TABLES 
                    WHERE TABLE_TYPE = 'BASE TABLE'
                    ORDER BY TABLE_SCHEMA, TABLE_NAME");

                foreach (var row in tablesResult.Rows) {
                    var schema = row[0]?.ToString() ?? "dbo";
                    var tableName = row[1]?.ToString();
                    if (string.IsNullOrEmpty(tableName)) continue;

                    var table = new TableSchema {
                        Name = tableName,
                        Schema = schema
                    };

                    // Get columns
                    var columnsResult = client.ExecuteQuery($@"
                        SELECT 
                            c.COLUMN_NAME,
                            c.DATA_TYPE,
                            c.IS_NULLABLE,
                            c.CHARACTER_MAXIMUM_LENGTH,
                            c.NUMERIC_PRECISION,
                            c.NUMERIC_SCALE,
                            c.COLUMN_DEFAULT,
                            COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsIdentity') as IsIdentity,
                            COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsComputed') as IsComputed
                        FROM INFORMATION_SCHEMA.COLUMNS c
                        WHERE c.TABLE_SCHEMA = '{schema}' AND c.TABLE_NAME = '{tableName}'
                        ORDER BY c.ORDINAL_POSITION");

                    foreach (var colRow in columnsResult.Rows) {
                        var column = new ColumnSchema {
                            Name = colRow[0]?.ToString(),
                            SqlType = colRow[1]?.ToString()?.ToLowerInvariant(),
                            IsNullable = colRow[2]?.ToString()?.Equals("YES", StringComparison.OrdinalIgnoreCase) ?? true,
                            MaxLength = colRow[3] as int?,
                            Precision = colRow[4] as int?,
                            Scale = colRow[5] as int?,
                            DefaultValue = colRow[6]?.ToString(),
                            IsIdentity = Convert.ToInt32(colRow[7] ?? 0) == 1,
                            IsComputed = Convert.ToInt32(colRow[8] ?? 0) == 1,
                            IsRowVersion = colRow[1]?.ToString()?.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ?? false
                        };
                        table.Columns.Add(column);
                    }

                    // Get primary keys
                    var pkResult = client.ExecuteQuery($@"
                        SELECT COLUMN_NAME
                        FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                        WHERE OBJECTPROPERTY(OBJECT_ID(CONSTRAINT_SCHEMA + '.' + CONSTRAINT_NAME), 'IsPrimaryKey') = 1
                        AND TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{tableName}'
                        ORDER BY ORDINAL_POSITION");

                    foreach (var pkRow in pkResult.Rows) {
                        var pkColumn = pkRow[0]?.ToString();
                        if (!string.IsNullOrEmpty(pkColumn)) {
                            table.PrimaryKeys.Add(pkColumn);
                            var col = table.Columns.FirstOrDefault(c => c.Name.Equals(pkColumn, StringComparison.OrdinalIgnoreCase));
                            if (col != null) col.IsPrimaryKey = true;
                        }
                    }

                    // Get foreign keys
                    var fkResult = client.ExecuteQuery($@"
                        SELECT 
                            fk.name AS FK_NAME,
                            COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS FK_COLUMN,
                            OBJECT_NAME(fkc.referenced_object_id) AS REFERENCED_TABLE,
                            COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS REFERENCED_COLUMN
                        FROM sys.foreign_keys fk
                        INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
                        WHERE fk.parent_object_id = OBJECT_ID('{schema}.{tableName}')");

                    foreach (var fkRow in fkResult.Rows) {
                        table.ForeignKeys.Add(new ForeignKeySchema {
                            Name = fkRow[0]?.ToString(),
                            ColumnName = fkRow[1]?.ToString(),
                            ReferencedTable = fkRow[2]?.ToString(),
                            ReferencedColumn = fkRow[3]?.ToString()
                        });
                    }

                    tables.Add(table);
                }
            }

            return tables;
        }

        #endregion

        #region Code Generation

        private string GenerateEntityClass(TableSchema table) {
            var className = ToPascalCase(table.Name);

            return Language == CodeLanguage.VisualBasic
                ? GenerateEntityClassVB(table, className)
                : GenerateEntityClassCS(table, className);
        }

        private string GenerateEntityClassCS(TableSchema table, string className) {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.ComponentModel.DataAnnotations;");
            sb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
            sb.AppendLine();
            sb.AppendLine($"namespace {Namespace} {{");
            sb.AppendLine();

            if (GeneratePartialClasses)
                sb.AppendLine($"    public partial class {className} {{");
            else
                sb.AppendLine($"    public class {className} {{");

            // Properties
            foreach (var column in table.Columns) {
                var propName = ToPascalCase(column.Name);
                var clrType = GetClrType(column);

                // Add attributes
                if (column.IsPrimaryKey)
                    sb.AppendLine("        [Key]");

                if (column.IsIdentity)
                    sb.AppendLine("        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]");

                if (!column.IsNullable && clrType == "string")
                    sb.AppendLine("        [Required]");

                if (column.MaxLength.HasValue && column.MaxLength > 0 && clrType == "string")
                    sb.AppendLine($"        [MaxLength({column.MaxLength})]");

                if (!propName.Equals(column.Name, StringComparison.Ordinal))
                    sb.AppendLine($"        [Column(\"{column.Name}\")]");

                // Property declaration
                var nullableSuffix = column.IsNullable && IsValueType(column) ? "?" : "";
                sb.AppendLine($"        public {clrType}{nullableSuffix} {EscapePropertyName(propName)} {{ get; set; }}");
                sb.AppendLine();
            }

            // Navigation properties
            if (IncludeNavigationProperties) {
                foreach (var fk in table.ForeignKeys) {
                    var navPropName = ToPascalCase(fk.ReferencedTable);
                    sb.AppendLine($"        public virtual {navPropName} {navPropName} {{ get; set; }}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string GenerateEntityClassVB(TableSchema table, string className) {
            var sb = new StringBuilder();
            sb.AppendLine("Imports System");
            sb.AppendLine("Imports System.Collections.Generic");
            sb.AppendLine("Imports System.ComponentModel.DataAnnotations");
            sb.AppendLine("Imports System.ComponentModel.DataAnnotations.Schema");
            sb.AppendLine();

            if (GeneratePartialClasses)
                sb.AppendLine($"Partial Public Class {className}");
            else
                sb.AppendLine($"Public Class {className}");

            // Properties
            foreach (var column in table.Columns) {
                var propName = ToPascalCase(column.Name);
                var vbType = GetVbType(column);

                // Add attributes
                if (column.IsPrimaryKey)
                    sb.AppendLine("    <Key>");

                if (column.IsIdentity)
                    sb.AppendLine("    <DatabaseGenerated(DatabaseGeneratedOption.Identity)>");

                if (!column.IsNullable && vbType == "String")
                    sb.AppendLine("    <Required>");

                if (column.MaxLength.HasValue && column.MaxLength > 0 && vbType == "String")
                    sb.AppendLine($"    <MaxLength({column.MaxLength})>");

                if (!propName.Equals(column.Name, StringComparison.Ordinal))
                    sb.AppendLine($"    <Column(\"{column.Name}\")>");

                // Property declaration
                var nullableSuffix = column.IsNullable && IsValueType(column) ? "?" : "";
                sb.AppendLine($"    Public Property {EscapePropertyNameVB(propName)} As {vbType}{nullableSuffix}");
            }

            // Navigation properties
            if (IncludeNavigationProperties) {
                foreach (var fk in table.ForeignKeys) {
                    var navPropName = ToPascalCase(fk.ReferencedTable);
                    sb.AppendLine($"    Public Overridable Property {navPropName} As {navPropName}");
                }
            }

            sb.AppendLine("End Class");

            return sb.ToString();
        }

        private string GenerateDbContextClass(List<TableSchema> tables) {
            return Language == CodeLanguage.VisualBasic
                ? GenerateDbContextClassVB(tables)
                : GenerateDbContextClassCS(tables);
        }

        private string GenerateDbContextClassCS(List<TableSchema> tables) {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using Microsoft.EntityFrameworkCore;");
            sb.AppendLine("using Microsoft.EntityFrameworkCore.Metadata;");
            sb.AppendLine();
            sb.AppendLine($"namespace {Namespace} {{");
            sb.AppendLine();

            var partial = GeneratePartialClasses ? "partial " : "";
            sb.AppendLine($"    public {partial}class {ContextName} : DbContext {{");
            sb.AppendLine();

            // Constructors
            sb.AppendLine($"        public {ContextName}() {{ }}");
            sb.AppendLine();
            sb.AppendLine($"        public {ContextName}(DbContextOptions<{ContextName}> options) : base(options) {{ }}");
            sb.AppendLine();

            // DbSet properties
            foreach (var table in tables) {
                var className = ToPascalCase(table.Name);
                var propName = Pluralize(className);
                sb.AppendLine($"        public virtual DbSet<{className}> {propName} {{ get; set; }}");
            }
            sb.AppendLine();

            // OnConfiguring
            sb.AppendLine("        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {");
            sb.AppendLine("            if (!optionsBuilder.IsConfigured) {");
            sb.AppendLine($"                optionsBuilder.UseSqlServer(\"{EscapeString(ConnectionString)}\");");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();

            // OnModelCreating
            sb.AppendLine("        protected override void OnModelCreating(ModelBuilder modelBuilder) {");

            foreach (var table in tables) {
                var className = ToPascalCase(table.Name);
                sb.AppendLine($"            modelBuilder.Entity<{className}>(entity => {{");

                // Primary key
                if (table.PrimaryKeys.Count == 1) {
                    var pk = ToPascalCase(table.PrimaryKeys[0]);
                    sb.AppendLine($"                entity.HasKey(e => e.{pk});");
                } else if (table.PrimaryKeys.Count > 1) {
                    var keys = string.Join(", ", table.PrimaryKeys.Select(k => $"e.{ToPascalCase(k)}"));
                    sb.AppendLine($"                entity.HasKey(e => new {{ {keys} }});");
                }

                // Table name if different from class name
                if (!table.Name.Equals(className, StringComparison.OrdinalIgnoreCase)) {
                    sb.AppendLine($"                entity.ToTable(\"{table.Name}\");");
                }

                // Column configurations
                foreach (var column in table.Columns) {
                    var propName = ToPascalCase(column.Name);
                    var configs = new List<string>();

                    if (!column.IsNullable && GetClrType(column) == "string")
                        configs.Add("IsRequired()");

                    if (column.MaxLength.HasValue && column.MaxLength > 0 && column.MaxLength < int.MaxValue)
                        configs.Add($"HasMaxLength({column.MaxLength})");

                    if (column.SqlType == "varchar" || column.SqlType == "char" || column.SqlType == "text")
                        configs.Add("IsUnicode(false)");

                    if (!propName.Equals(column.Name, StringComparison.Ordinal))
                        configs.Add($"HasColumnName(\"{column.Name}\")");

                    if (column.IsRowVersion)
                        configs.Add("IsRowVersion().IsConcurrencyToken()");

                    if (column.SqlType == "datetime" || column.SqlType == "datetime2" || column.SqlType == "date")
                        configs.Add($"HasColumnType(\"{column.SqlType}\")");

                    if (configs.Count > 0) {
                        sb.AppendLine($"                entity.Property(e => e.{propName}).{string.Join(".", configs)};");
                    }
                }

                // Foreign key relationships
                foreach (var fk in table.ForeignKeys) {
                    var navProp = ToPascalCase(fk.ReferencedTable);
                    var fkProp = ToPascalCase(fk.ColumnName);
                    sb.AppendLine($"                entity.HasOne(e => e.{navProp})");
                    sb.AppendLine($"                    .WithMany()");
                    sb.AppendLine($"                    .HasForeignKey(e => e.{fkProp})");
                    sb.AppendLine($"                    .HasConstraintName(\"{fk.Name}\");");
                }

                sb.AppendLine("            });");
                sb.AppendLine();
            }

            if (GeneratePartialClasses)
                sb.AppendLine("            OnModelCreatingPartial(modelBuilder);");

            sb.AppendLine("        }");
            sb.AppendLine();

            if (GeneratePartialClasses)
                sb.AppendLine("        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string GenerateDbContextClassVB(List<TableSchema> tables) {
            var sb = new StringBuilder();
            sb.AppendLine("Imports Microsoft.EntityFrameworkCore");
            sb.AppendLine("Imports Microsoft.EntityFrameworkCore.Metadata");
            sb.AppendLine();

            var partial = GeneratePartialClasses ? "Partial " : "";
            sb.AppendLine($"{partial}Public Class {ContextName}");
            sb.AppendLine("    Inherits DbContext");
            sb.AppendLine();

            // Constructors
            sb.AppendLine("    Public Sub New()");
            sb.AppendLine("    End Sub");
            sb.AppendLine();
            sb.AppendLine($"    Public Sub New(options As DbContextOptions(Of {ContextName}))");
            sb.AppendLine("        MyBase.New(options)");
            sb.AppendLine("    End Sub");
            sb.AppendLine();

            // DbSet properties
            foreach (var table in tables) {
                var className = ToPascalCase(table.Name);
                var propName = Pluralize(className);
                sb.AppendLine($"    Public Overridable Property {propName} As DbSet(Of {className})");
            }
            sb.AppendLine();

            // OnConfiguring
            sb.AppendLine("    Protected Overrides Sub OnConfiguring(optionsBuilder As DbContextOptionsBuilder)");
            sb.AppendLine("        If Not optionsBuilder.IsConfigured Then");
            sb.AppendLine($"            optionsBuilder.UseSqlServer(\"{EscapeStringVB(ConnectionString)}\")");
            sb.AppendLine("        End If");
            sb.AppendLine("    End Sub");
            sb.AppendLine();

            // OnModelCreating
            sb.AppendLine("    Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)");

            foreach (var table in tables) {
                var className = ToPascalCase(table.Name);
                sb.AppendLine($"        modelBuilder.Entity(Of {className})(Sub(entity)");

                // Primary key
                if (table.PrimaryKeys.Count == 1) {
                    var pk = ToPascalCase(table.PrimaryKeys[0]);
                    sb.AppendLine($"            entity.HasKey(Function(e) e.{pk})");
                } else if (table.PrimaryKeys.Count > 1) {
                    var keys = string.Join(", ", table.PrimaryKeys.Select(k => $"e.{ToPascalCase(k)}"));
                    sb.AppendLine($"            entity.HasKey(Function(e) New With {{{keys}}})");
                }

                // Column configurations
                foreach (var column in table.Columns) {
                    var propName = ToPascalCase(column.Name);
                    var configs = new List<string>();

                    if (!column.IsNullable && GetVbType(column) == "String")
                        configs.Add("IsRequired()");

                    if (column.MaxLength.HasValue && column.MaxLength > 0 && column.MaxLength < int.MaxValue)
                        configs.Add($"HasMaxLength({column.MaxLength})");

                    if (column.SqlType == "varchar" || column.SqlType == "char" || column.SqlType == "text")
                        configs.Add("IsUnicode(False)");

                    if (!propName.Equals(column.Name, StringComparison.Ordinal))
                        configs.Add($"HasColumnName(\"{column.Name}\")");

                    if (column.IsRowVersion)
                        configs.Add("IsRowVersion().IsConcurrencyToken()");

                    if (column.SqlType == "datetime" || column.SqlType == "datetime2" || column.SqlType == "date")
                        configs.Add($"HasColumnType(\"{column.SqlType}\")");

                    if (configs.Count > 0) {
                        sb.AppendLine($"            entity.[Property](Function(e) e.{propName}).{string.Join(".", configs)}");
                    }
                }

                sb.AppendLine("        End Sub)");
                sb.AppendLine();
            }

            if (GeneratePartialClasses)
                sb.AppendLine("        Me.OnModelCreatingPartial(modelBuilder)");

            sb.AppendLine("    End Sub");
            sb.AppendLine();

            if (GeneratePartialClasses) {
                sb.AppendLine("    Partial Private Sub OnModelCreatingPartial(modelBuilder As ModelBuilder)");
                sb.AppendLine("    End Sub");
            }

            sb.AppendLine("End Class");

            return sb.ToString();
        }

        #endregion

        #region MSBuild Integration

        /// <summary>
        /// Generates an MSBuild targets file for automatic code generation during build.
        /// </summary>
        public string GenerateMSBuildTargets() {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine($"    <DbContextGeneratorConnectionString>{EscapeXml(ConnectionString)}</DbContextGeneratorConnectionString>");
            sb.AppendLine($"    <DbContextGeneratorNamespace>{Namespace}</DbContextGeneratorNamespace>");
            sb.AppendLine($"    <DbContextGeneratorContextName>{ContextName}</DbContextGeneratorContextName>");
            sb.AppendLine($"    <DbContextGeneratorOutputDir>$(MSBuildProjectDirectory)\\Generated</DbContextGeneratorOutputDir>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <Target Name=\"GenerateDbContext\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' != 'true'\">");
            sb.AppendLine("    <Message Importance=\"high\" Text=\"Generating DbContext from database schema...\" />");
            sb.AppendLine("    <Exec Command=\"dotnet tool run dbcontext-generator --connection &quot;$(DbContextGeneratorConnectionString)&quot; --namespace $(DbContextGeneratorNamespace) --context $(DbContextGeneratorContextName) --output &quot;$(DbContextGeneratorOutputDir)&quot;\" />");
            sb.AppendLine("  </Target>");
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Compile Include=\"Generated\\*.cs\" Condition=\"'$(Language)' == 'C#'\" />");
            sb.AppendLine("    <Compile Include=\"Generated\\*.vb\" Condition=\"'$(Language)' == 'VB'\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("</Project>");

            return sb.ToString();
        }

        /// <summary>
        /// Generates a pre-build event command for Visual Studio.
        /// </summary>
        public string GeneratePreBuildEvent() {
            var lang = Language == CodeLanguage.VisualBasic ? "vb" : "cs";
            return $"\"$(SolutionDir)tools\\DbContextGenerator.exe\" --connection \"{ConnectionString}\" --namespace {Namespace} --context {ContextName} --language {lang} --output \"$(ProjectDir)Generated\"";
        }

        #endregion

        #region Helper Methods

        private string GetClrType(ColumnSchema column) {
            return column.SqlType switch {
                "bigint" => "long",
                "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "byte[]",
                "bit" => "bool",
                "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" => "string",
                "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
                "datetimeoffset" => "DateTimeOffset",
                "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
                "float" => "double",
                "int" => "int",
                "real" => "float",
                "smallint" => "short",
                "tinyint" => "byte",
                "uniqueidentifier" => "Guid",
                "time" => "TimeSpan",
                "xml" => "string",
                _ => "object"
            };
        }

        private string GetVbType(ColumnSchema column) {
            return column.SqlType switch {
                "bigint" => "Long",
                "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "Byte()",
                "bit" => "Boolean",
                "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" => "String",
                "date" or "datetime" or "datetime2" or "smalldatetime" => "Date",
                "datetimeoffset" => "DateTimeOffset",
                "decimal" or "numeric" or "money" or "smallmoney" => "Decimal",
                "float" => "Double",
                "int" => "Integer",
                "real" => "Single",
                "smallint" => "Short",
                "tinyint" => "Byte",
                "uniqueidentifier" => "Guid",
                "time" => "TimeSpan",
                "xml" => "String",
                _ => "Object"
            };
        }

        private bool IsValueType(ColumnSchema column) {
            var type = column.SqlType;
            return type != "char" && type != "varchar" && type != "text" &&
                   type != "nchar" && type != "nvarchar" && type != "ntext" &&
                   type != "binary" && type != "varbinary" && type != "image" &&
                   type != "xml";
        }

        private string ToPascalCase(string name) {
            if (string.IsNullOrEmpty(name)) return name;

            var sb = new StringBuilder();
            bool capitalizeNext = true;

            foreach (char c in name) {
                if (c == '_' || c == ' ' || c == '-') {
                    capitalizeNext = true;
                } else if (capitalizeNext) {
                    sb.Append(char.ToUpperInvariant(c));
                    capitalizeNext = false;
                } else {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private string Pluralize(string name) {
            if (string.IsNullOrEmpty(name)) return name;

            if (name.EndsWith("y", StringComparison.OrdinalIgnoreCase) && 
                name.Length > 1 && 
                !"aeiou".Contains(char.ToLowerInvariant(name[name.Length - 2]))) {
                return name.Substring(0, name.Length - 1) + "ies";
            }
            if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("sh", StringComparison.OrdinalIgnoreCase)) {
                return name + "es";
            }
            return name + "s";
        }

        private string EscapePropertyName(string name) {
            // C# reserved keywords
            var keywords = new HashSet<string> { 
                "abstract", "as", "base", "bool", "break", "byte", "case", "catch", 
                "char", "checked", "class", "const", "continue", "decimal", "default", 
                "delegate", "do", "double", "else", "enum", "event", "explicit", 
                "extern", "false", "finally", "fixed", "float", "for", "foreach", 
                "goto", "if", "implicit", "in", "int", "interface", "internal", 
                "is", "lock", "long", "namespace", "new", "null", "object", "operator", 
                "out", "override", "params", "private", "protected", "public", 
                "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", 
                "stackalloc", "static", "string", "struct", "switch", "this", "throw", 
                "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", 
                "ushort", "using", "virtual", "void", "volatile", "while" 
            };

            return keywords.Contains(name.ToLowerInvariant()) ? "@" + name : name;
        }

        private string EscapePropertyNameVB(string name) {
            // VB.NET reserved keywords
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { 
                "AddHandler", "AddressOf", "Alias", "And", "AndAlso", "As", "Boolean", 
                "ByRef", "Byte", "ByVal", "Call", "Case", "Catch", "CBool", "CByte", 
                "CChar", "CDate", "CDbl", "CDec", "Char", "CInt", "Class", "CLng", 
                "CObj", "Const", "Continue", "CSByte", "CShort", "CSng", "CStr", 
                "CType", "CUInt", "CULng", "CUShort", "Date", "Decimal", "Declare", 
                "Default", "Delegate", "Dim", "DirectCast", "Do", "Double", "Each", 
                "Else", "ElseIf", "End", "EndIf", "Enum", "Erase", "Error", "Event", 
                "Exit", "False", "Finally", "For", "Friend", "Function", "Get", 
                "GetType", "GetXMLNamespace", "Global", "GoSub", "GoTo", "Handles", 
                "If", "Implements", "Imports", "In", "Inherits", "Integer", "Interface", 
                "Is", "IsNot", "Let", "Lib", "Like", "Long", "Loop", "Me", "Mod", 
                "Module", "MustInherit", "MustOverride", "MyBase", "MyClass", "Namespace", 
                "Narrowing", "New", "Next", "Not", "Nothing", "NotInheritable", 
                "NotOverridable", "Object", "Of", "On", "Operator", "Option", "Optional", 
                "Or", "OrElse", "Overloads", "Overridable", "Overrides", "ParamArray", 
                "Partial", "Private", "Property", "Protected", "Public", "RaiseEvent", 
                "ReadOnly", "ReDim", "REM", "RemoveHandler", "Resume", "Return", 
                "SByte", "Select", "Set", "Shadows", "Shared", "Short", "Single", 
                "Static", "Step", "Stop", "String", "Structure", "Sub", "SyncLock", 
                "Then", "Throw", "To", "True", "Try", "TryCast", "TypeOf", "UInteger", 
                "ULong", "UShort", "Using", "Variant", "Wend", "When", "While", 
                "Widening", "With", "WithEvents", "WriteOnly", "Xor" 
            };

            return keywords.Contains(name) ? "[" + name + "]" : name;
        }

        private string GetFileExtension() {
            return Language == CodeLanguage.VisualBasic ? ".vb" : ".cs";
        }

        private string EscapeString(string s) {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        }

        private string EscapeStringVB(string s) {
            return s?.Replace("\"", "\"\"") ?? "";
        }

        private string EscapeXml(string s) {
            return s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;") ?? "";
        }

        private void ReportProgress(string message) {
            Progress?.Invoke(this, new GeneratorProgressEventArgs { Message = message });
        }

        #endregion
    }

    #region Enums and Result Classes

    public enum CodeLanguage {
        CSharp,
        VisualBasic
    }

    public class GenerationResult {
        public bool Success { get; set; }
        public string Error { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, string> GeneratedFiles { get; set; } = new Dictionary<string, string>();
        public List<string> WrittenFiles { get; set; } = new List<string>();
    }

    public class GeneratorProgressEventArgs : EventArgs {
        public string Message { get; set; }
    }

    #endregion
}
