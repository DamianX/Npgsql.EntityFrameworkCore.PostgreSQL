﻿#region License

// The PostgreSQL License
//
// Copyright (C) 2016 The Npgsql Development Team
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.

#endregion

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql.EntityFrameworkCore.PostgreSQL.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Utilities;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Scaffolding.Internal
{
    /// <summary>
    /// The default database model factory for Npgsql.
    /// </summary>
    public class NpgsqlDatabaseModelFactory : IDatabaseModelFactory
    {
        #region Fields

        /// <summary>
        /// The regular expression formatting string for schema and/or table names.
        /// </summary>
        [NotNull] const string NamePartRegex = @"(?:(?:""(?<part{0}>(?:(?:"""")|[^""])+)"")|(?<part{0}>[^\.\[""]+))";

        /// <summary>
        /// The <see cref="Regex"/> to extract the schema and/or table names.
        /// </summary>
        [NotNull] static readonly Regex SchemaTableNameExtractor =
            new Regex(
                string.Format(
                    CultureInfo.InvariantCulture,
                    @"^{0}(?:\.{1})?$",
                    string.Format(CultureInfo.InvariantCulture, NamePartRegex, 1),
                    string.Format(CultureInfo.InvariantCulture, NamePartRegex, 2)),
                RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(1000.0));

        /// <summary>
        /// Tables which are considered to be system tables and should not get scaffolded, e.g. the support table
        /// created by the PostGIS extension.
        /// </summary>
        [NotNull] [ItemNotNull] static readonly string[] SystemTables = { "spatial_ref_sys" };

        /// <summary>
        /// The types used for serial columns.
        /// </summary>
        [NotNull] [ItemNotNull] static readonly string[] SerialTypes = { "int2", "int4", "int8" };

        /// <summary>
        /// The diagnostic logger instance.
        /// </summary>
        [NotNull] readonly IDiagnosticsLogger<DbLoggerCategory.Scaffolding> _logger;

        #endregion

        #region Public surface

        /// <summary>
        /// Constructs an instance of the <see cref="NpgsqlDatabaseModelFactory"/> class.
        /// </summary>
        /// <param name="logger">The diagnostic logger instance.</param>
        public NpgsqlDatabaseModelFactory([NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
            => _logger = Check.NotNull(logger, nameof(logger));

        /// <inheritdoc />
        public virtual DatabaseModel Create(string connectionString, IEnumerable<string> tables, IEnumerable<string> schemas)
        {
            Check.NotEmpty(connectionString, nameof(connectionString));
            Check.NotNull(tables, nameof(tables));
            Check.NotNull(schemas, nameof(schemas));

            using (var connection = new NpgsqlConnection(connectionString))
            {
                return Create(connection, tables, schemas);
            }
        }

        /// <inheritdoc />
        public virtual DatabaseModel Create(DbConnection dbConnection, IEnumerable<string> tables, IEnumerable<string> schemas)
        {
            Check.NotNull(dbConnection, nameof(dbConnection));
            Check.NotNull(tables, nameof(tables));
            Check.NotNull(schemas, nameof(schemas));

            var connection = (NpgsqlConnection)dbConnection;

            var connectionStartedOpen = connection.State == ConnectionState.Open;

            if (!connectionStartedOpen)
                connection.Open();

            try
            {
                var databaseModel = new DatabaseModel
                {
                    DatabaseName = connection.Database,
                    DefaultSchema = "public"
                };

                var schemaList = schemas.ToList();
                var schemaFilter = GenerateSchemaFilter(schemaList);
                var tableList = tables.ToList();
                var tableFilter = GenerateTableFilter(tableList.Select(ParseSchemaTable).ToList(), schemaFilter);

                var enums = GetEnums(connection, databaseModel);

                foreach (var table in GetTables(connection, tableFilter, enums, _logger))
                {
                    table.Database = databaseModel;
                    databaseModel.Tables.Add(table);
                }

                foreach (var table in databaseModel.Tables)
                {
                    while (table.Columns.Remove(null)) {}
                }

                foreach (var sequence in GetSequences(connection, databaseModel.Tables, schemaFilter, _logger))
                {
                    sequence.Database = databaseModel;
                    databaseModel.Sequences.Add(sequence);
                }

                GetExtensions(connection, databaseModel);

                for (var i = 0; i < databaseModel.Tables.Count; i++)
                {
                    var table = databaseModel.Tables[i];

                    // Remove some tables which shouldn't get scaffolded, unless they're explicitly mentioned
                    // in the table list
                    if (SystemTables.Contains(table.Name) && !tableList.Contains(table.Name))
                    {
                        databaseModel.Tables.RemoveAt(i);
                        continue;
                    }

                    // We may have dropped or skipped columns. We load these because constraints take them into
                    // account when referencing columns, but must now get rid of them before returning
                    // the database model.
                    while (table.Columns.Remove(null)) {}
                }

                foreach (var schema in schemaList
                    .Except(databaseModel.Sequences.Select(s => s.Schema).Concat(databaseModel.Tables.Select(t => t.Schema))))
                {
                    _logger.MissingSchemaWarning(schema);
                }

                foreach (var table in tableList)
                {
                    var (schema, name) = ParseSchemaTable(table);
                    if (!databaseModel.Tables.Any(t => !string.IsNullOrEmpty(schema) && t.Schema == schema || t.Name == name))
                        _logger.MissingTableWarning(table);
                }

                return databaseModel;
            }
            finally
            {
                if (!connectionStartedOpen)
                    connection.Close();
            }
        }

        #endregion

        #region Type information queries

        /// <summary>
        /// Queries the database for defined tables and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="tableFilter">The table filter fragment.</param>
        /// <param name="enums">The collection of discovered enums.</param>
        /// <param name="logger">The diagnostic logger.</param>
        /// <returns>
        /// A collection of tables defined in the database.
        /// </returns>
        [NotNull]
        static IEnumerable<DatabaseTable> GetTables(
            [NotNull] NpgsqlConnection connection,
            [CanBeNull] Func<string, string, string> tableFilter,
            [NotNull] [ItemNotNull] HashSet<string> enums,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            var filter = tableFilter != null ? $"AND {tableFilter("ns.nspname", "cls.relname")}" : null;
            var commandText = $@"
SELECT nspname, relname, description
FROM pg_class AS cls
JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
LEFT OUTER JOIN pg_description AS des ON des.objoid = cls.oid AND des.objsubid=0
WHERE
  cls.relkind = 'r' AND
  ns.nspname NOT IN ('pg_catalog', 'information_schema') AND
  cls.relname <> '{HistoryRepository.DefaultTableName}'
  {filter}";

            var tables = new List<DatabaseTable>();

            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var table = new DatabaseTable
                    {
                        Schema = reader.GetValueOrDefault<string>("nspname"),
                        Name = reader.GetValueOrDefault<string>("relname")
                    };

                    if (reader.GetValueOrDefault<string>("description") is string comment)
                        table[NpgsqlAnnotationNames.Comment] = comment;

                    tables.Add(table);
                }
            }

            GetColumns(connection, tables, filter, enums, logger);
            GetConstraints(connection, tables, filter, out var constraintIndexes, logger);
            GetIndexes(connection, tables, filter, constraintIndexes, logger);
            return tables;
        }

        /// <summary>
        /// Queries the database for defined columns and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="tables">The database tables.</param>
        /// <param name="tableFilter">The table filter fragment.</param>
        /// <param name="enums">The collection of discovered enums.</param>
        /// <param name="logger">The diagnostic logger.</param>
        static void GetColumns(
            [NotNull] NpgsqlConnection connection,
            [NotNull] IReadOnlyList<DatabaseTable> tables,
            [CanBeNull] string tableFilter,
            [NotNull] HashSet<string> enums,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            var commandText = $@"
SELECT
  nspname,
  relname,
  typ.typname,
  basetyp.typname AS basetypname,
  attname,
  description,
  attisdropped,
  {(connection.PostgreSqlVersion >= new Version(10, 0) ? "attidentity" : "''::\"char\" as attidentity")},
  format_type(typ.oid, atttypmod) AS formatted_typname,
  format_type(basetyp.oid, typ.typtypmod) AS formatted_basetypname,
  CASE
    WHEN pg_proc.proname = 'array_recv' THEN 'a'
    ELSE typ.typtype
  END AS typtype,
  CASE
    WHEN pg_proc.proname='array_recv' THEN elemtyp.typname
    ELSE NULL
  END AS elemtypname,
  (NOT attnotnull) AS nullable,
  CASE
    WHEN atthasdef THEN (SELECT pg_get_expr(adbin, cls.oid) FROM pg_attrdef WHERE adrelid = cls.oid AND adnum = attr.attnum)
    ELSE NULL
  END AS default
FROM pg_class AS cls
JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
LEFT OUTER JOIN pg_attribute AS attr ON attrelid = cls.oid
LEFT OUTER JOIN pg_type AS typ ON attr.atttypid = typ.oid
LEFT OUTER JOIN pg_proc ON pg_proc.oid = typ.typreceive
LEFT OUTER JOIN pg_type AS elemtyp ON (elemtyp.oid = typ.typelem)
LEFT OUTER JOIN pg_type AS basetyp ON (basetyp.oid = typ.typbasetype)
LEFT OUTER JOIN pg_description AS des ON des.objoid = cls.oid AND des.objsubid = attnum
WHERE
  relkind = 'r' AND
  nspname NOT IN ('pg_catalog', 'information_schema') AND
  attnum > 0 AND
  cls.relname <> '{HistoryRepository.DefaultTableName}'
  {tableFilter}
ORDER BY attnum";

            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                var tableGroups = reader.Cast<DbDataRecord>().GroupBy(ddr => (
                    tableSchema: ddr.GetValueOrDefault<string>("nspname"),
                    tableName: ddr.GetValueOrDefault<string>("relname")));

                foreach (var tableGroup in tableGroups)
                {
                    var tableSchema = tableGroup.Key.tableSchema;
                    var tableName = tableGroup.Key.tableName;

                    var table = tables.Single(t => t.Schema == tableSchema && t.Name == tableName);

                    foreach (var record in tableGroup)
                    {
                        var column = new DatabaseColumn
                        {
                            Table = table,
                            Name = record.GetValueOrDefault<string>("attname"),
                            IsNullable = record.GetValueOrDefault<bool>("nullable"),
                            DefaultValueSql = record.GetValueOrDefault<string>("default"),
                            ComputedColumnSql = null
                        };

                        // We need to know about dropped columns because constraints take them into
                        // account when referencing columns. We'll get rid of them before returning the model.
                        if (record.GetValueOrDefault<bool>("attisdropped"))
                        {
                            table.Columns.Add(null);
                            continue;
                        }

                        string systemTypeName;
                        var formattedTypeName = AdjustFormattedTypeName(record.GetValueOrDefault<string>("formatted_typname"));
                        var formattedBaseTypeName = record.GetValueOrDefault<string>("formatted_basetypname");
                        if (formattedBaseTypeName == null)
                        {
                            column.StoreType = formattedTypeName;
                            systemTypeName = record.GetValueOrDefault<string>("typname");
                        }
                        else
                        {
                            // This is a domain type
                            column.StoreType = formattedTypeName;
                            column.SetUnderlyingStoreType(AdjustFormattedTypeName(formattedBaseTypeName));
                            systemTypeName = record.GetValueOrDefault<string>("basetypname");
                        }

                        // Enum types cannot be scaffolded for now (nor can domains of enum types),
                        // skip with an informative message
                        if (enums.Contains(formattedTypeName) || enums.Contains(formattedBaseTypeName))
                        {
                            logger.EnumColumnSkippedWarning($"{DisplayName(tableSchema, tableName)}.{column.Name}");
                            // We need to know about skipped columns because constraints take them into
                            // account when referencing columns. We'll get rid of them before returning the model.
                            table.Columns.Add(null);
                            continue;
                        }

                        logger.ColumnFound(
                            DisplayName(tableSchema, tableName),
                            column.Name,
                            formattedTypeName,
                            column.IsNullable,
                            column.DefaultValueSql);

                        // Identify IDENTITY columns, as well as SERIAL ones.
                        switch (record.GetValueOrDefault<char>("attidentity"))
                        {
                        case 'a':
                            column[NpgsqlAnnotationNames.ValueGenerationStrategy] = NpgsqlValueGenerationStrategy.IdentityAlwaysColumn;
                            break;
                        case 'd':
                            column[NpgsqlAnnotationNames.ValueGenerationStrategy] = NpgsqlValueGenerationStrategy.IdentityByDefaultColumn;
                            break;
                        default:
                            if (SerialTypes.Contains(systemTypeName) &&
                                column.DefaultValueSql == $"nextval('{column.Table.Name}_{column.Name}_seq'::regclass)" ||
                                column.DefaultValueSql == $"nextval('\"{column.Table.Name}_{column.Name}_seq\"'::regclass)")
                            {
                                // Hacky but necessary...
                                // We identify serial columns by examining their default expression,
                                // and reverse-engineer these as ValueGenerated.OnAdd
                                // TODO: Think about composite keys? Do serial magic only for non-composite.
                                column.DefaultValueSql = null;
                                // Serial is the default value generation strategy, so NpgsqlAnnotationCodeGenerator
                                // makes sure it isn't actually rendered
                                column[NpgsqlAnnotationNames.ValueGenerationStrategy] = NpgsqlValueGenerationStrategy.SerialColumn;
                            }

                            break;
                        }

                        if (column[NpgsqlAnnotationNames.ValueGenerationStrategy] != null)
                            column.ValueGenerated = ValueGenerated.OnAdd;

                        AdjustDefaults(column, systemTypeName);

                        if (record.GetValueOrDefault<string>("description") is string comment)
                            column[NpgsqlAnnotationNames.Comment] = comment;

                        table.Columns.Add(column);
                    }
                }
            }
        }

        /// <summary>
        /// Queries the database for defined indexes and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="tables">The database tables.</param>
        /// <param name="tableFilter">The table filter fragment.</param>
        /// <param name="constraintIndexes">The constraint indexes.</param>
        /// <param name="logger">The diagnostic logger.</param>
        static void GetIndexes(
            [NotNull] NpgsqlConnection connection,
            [NotNull] IReadOnlyList<DatabaseTable> tables,
            [CanBeNull] string tableFilter,
            [NotNull] List<uint> constraintIndexes,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            var commandText = $@"
SELECT
  idxcls.oid AS idx_oid,
  nspname,
  cls.relname AS cls_relname,
  idxcls.relname AS idx_relname,
  indisunique,
  indkey,
  amname,
  CASE
    WHEN indexprs IS NULL THEN NULL
    ELSE pg_get_expr(indexprs, cls.oid)
  END AS exprs,
  CASE
    WHEN indpred IS NULL THEN NULL
    ELSE pg_get_expr(indpred, cls.oid)
  END AS pred
FROM pg_class AS cls
JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
JOIN pg_index AS idx ON indrelid = cls.oid
JOIN pg_class AS idxcls ON idxcls.oid = indexrelid
JOIN pg_am AS am ON am.oid = idxcls.relam
WHERE
  cls.relkind = 'r' AND
  nspname NOT IN ('pg_catalog', 'information_schema') AND
  NOT indisprimary AND
  cls.relname <> '{HistoryRepository.DefaultTableName}'
  {tableFilter}";

            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                var tableGroups = reader.Cast<DbDataRecord>().GroupBy(ddr => (
                    tableSchema: ddr.GetValueOrDefault<string>("nspname"),
                    tableName: ddr.GetValueOrDefault<string>("cls_relname")));

                foreach (var tableGroup in tableGroups)
                {
                    var tableSchema = tableGroup.Key.tableSchema;
                    var tableName = tableGroup.Key.tableName;

                    var table = tables.Single(t => t.Schema == tableSchema && t.Name == tableName);

                    foreach (var record in tableGroup)
                    {
                        // Constraints are detected separately (see GetConstraints), and we don't want their
                        // supporting indexes to appear independently.
                        if (constraintIndexes.Contains(record.GetValueOrDefault<uint>("idx_oid")))
                            continue;

                        var index = new DatabaseIndex
                        {
                            Table = table,
                            Name = record.GetValueOrDefault<string>("idx_relname"),
                            IsUnique = record.GetValueOrDefault<bool>("indisunique")
                        };

                        var columnIndices = record.GetValueOrDefault<short[]>("indkey");
                        if (columnIndices.Any(i => i == 0))
                        {
                            // Expression index, not supported
                            logger.ExpressionIndexSkippedWarning(index.Name, DisplayName(tableSchema, tableName));
                            continue;

                            /*
                            var expressions = record.GetValueOrDefault<string>("exprs");
                            if (expressions == null)
                                throw new Exception($"Seen 0 in indkey for index {index.Name} but indexprs is null");
                            index[NpgsqlAnnotationNames.IndexExpression] = expressions;
                            */
                        }

                        var columns = (List<DatabaseColumn>)table.Columns;
                        foreach (var i in columnIndices)
                        {
                            if (columns[i - 1] is DatabaseColumn indexColumn)
                                index.Columns.Add(indexColumn);

                            else
                            {
                                logger.UnsupportedColumnIndexSkippedWarning(index.Name, DisplayName(tableSchema, tableName));
                                goto IndexEnd;
                            }
                        }

                        if (record.GetValueOrDefault<string>("pred") is string predicate)
                            index.Filter = predicate;

                        // It's cleaner to always output the index method on the database model,
                        // even when it's btree (the default);
                        // NpgsqlAnnotationCodeGenerator can then omit it as by-convention.
                        // However, because of https://github.com/aspnet/EntityFrameworkCore/issues/11846 we omit
                        // the annotation from the model entirely.
                        if (record.GetValueOrDefault<string>("amname") is string indexMethod && indexMethod != "btree")
                            index[NpgsqlAnnotationNames.IndexMethod] = indexMethod;

                        table.Indexes.Add(index);

                        IndexEnd: ;
                    }
                }
            }
        }

        /// <summary>
        /// Queries the database for defined constraints and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="tables">The database tables.</param>
        /// <param name="tableFilter">The table filter fragment.</param>
        /// <param name="constraintIndexes">The constraint indexes.</param>
        /// <param name="logger">The diagnostic logger.</param>
        /// <exception cref="InvalidOperationException">Found varying lengths for column and principal column indices.</exception>
        static void GetConstraints(
            [NotNull] NpgsqlConnection connection,
            [NotNull] IReadOnlyList<DatabaseTable> tables,
            [CanBeNull] string tableFilter,
            [NotNull] out List<uint> constraintIndexes,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            var commandText = $@"
SELECT
  ns.nspname,
  cls.relname,
  conname,
  contype,
  conkey,
  conindid,
  frnns.nspname AS fr_nspname,
  frncls.relname AS fr_relname,
  confkey,
  confdeltype
FROM pg_class AS cls
JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
JOIN pg_constraint as con ON con.conrelid = cls.oid
LEFT OUTER JOIN pg_class AS frncls ON frncls.oid = con.confrelid
LEFT OUTER JOIN pg_namespace as frnns ON frnns.oid = frncls.relnamespace
WHERE
  cls.relkind = 'r' AND
  ns.nspname NOT IN ('pg_catalog', 'information_schema') AND
  con.contype IN ('p', 'f', 'u') AND
  cls.relname <> '{HistoryRepository.DefaultTableName}'
  {tableFilter}";

            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                constraintIndexes = new List<uint>();
                var tableGroups = reader.Cast<DbDataRecord>().GroupBy(ddr => (
                    tableSchema: ddr.GetValueOrDefault<string>("nspname"),
                    tableName: ddr.GetValueOrDefault<string>("relname")));

                foreach (var tableGroup in tableGroups)
                {
                    var tableSchema = tableGroup.Key.tableSchema;
                    var tableName = tableGroup.Key.tableName;

                    var table = tables.Single(t => t.Schema == tableSchema && t.Name == tableName);

                    // Primary keys
                    foreach (var primaryKeyRecord in tableGroup.Where(ddr => ddr.GetValueOrDefault<char>("contype") == 'p'))
                    {
                        var primaryKey = new DatabasePrimaryKey
                        {
                            Table = table,
                            Name = primaryKeyRecord.GetValueOrDefault<string>("conname")
                        };

                        foreach (var pkColumnIndex in primaryKeyRecord.GetValueOrDefault<short[]>("conkey"))
                        {
                            if (table.Columns[pkColumnIndex - 1] is DatabaseColumn pkColumn)
                                primaryKey.Columns.Add(pkColumn);

                            else
                            {
                                logger.UnsupportedColumnConstraintSkippedWarning(primaryKey.Name, DisplayName(tableSchema, tableName));
                                goto PkEnd;
                            }
                        }

                        table.PrimaryKey = primaryKey;
                        PkEnd: ;
                    }

                    // Foreign keys
                    foreach (var foreignKeyRecord in tableGroup.Where(ddr => ddr.GetValueOrDefault<char>("contype") == 'f'))
                    {
                        var fkName = foreignKeyRecord.GetValueOrDefault<string>("conname");
                        var principalTableSchema = foreignKeyRecord.GetValueOrDefault<string>("fr_nspname");
                        var principalTableName = foreignKeyRecord.GetValueOrDefault<string>("fr_relname");
                        var onDeleteAction = foreignKeyRecord.GetValueOrDefault<char>("confdeltype");

                        var principalTable =
                            tables.FirstOrDefault(t =>
                                t.Schema == principalTableSchema &&
                                t.Name == principalTableName)
                            ?? tables.FirstOrDefault(t =>
                                t.Schema.Equals(principalTableSchema, StringComparison.OrdinalIgnoreCase) &&
                                t.Name.Equals(principalTableName, StringComparison.OrdinalIgnoreCase));

                        if (principalTable == null)
                        {
                            logger.ForeignKeyReferencesMissingPrincipalTableWarning(
                                fkName,
                                DisplayName(table.Schema, table.Name),
                                DisplayName(principalTableSchema, principalTableName));

                            continue;
                        }

                        var foreignKey = new DatabaseForeignKey
                        {
                            Name = fkName,
                            Table = table,
                            PrincipalTable = principalTable,
                            OnDelete = ConvertToReferentialAction(onDeleteAction)
                        };

                        var columnIndices = foreignKeyRecord.GetValueOrDefault<short[]>("conkey");
                        var principalColumnIndices = foreignKeyRecord.GetValueOrDefault<short[]>("confkey");

                        if (columnIndices.Length != principalColumnIndices.Length)
                            throw new InvalidOperationException("Found varying lengths for column and principal column indices.");

                        var principalColumns = (List<DatabaseColumn>)principalTable.Columns;

                        for (var i = 0; i < columnIndices.Length; i++)
                        {
                            var foreignKeyColumn = table.Columns[columnIndices[i] - 1];
                            var foreignKeyPrincipalColumn = principalColumns[principalColumnIndices[i] - 1];
                            if (foreignKeyColumn == null || foreignKeyPrincipalColumn == null)
                            {
                                logger.UnsupportedColumnConstraintSkippedWarning(foreignKey.Name, DisplayName(tableSchema, tableName));
                                goto ForeignKeyEnd;
                            }

                            foreignKey.Columns.Add(foreignKeyColumn);
                            foreignKey.PrincipalColumns.Add(foreignKeyPrincipalColumn);
                        }

                        table.ForeignKeys.Add(foreignKey);
                        ForeignKeyEnd: ;
                    }

                    // Unique constraints
                    foreach (var record in tableGroup.Where(ddr => ddr.GetValueOrDefault<char>("contype") == 'u'))
                    {
                        var name = record.GetValueOrDefault<string>("conname");

                        logger.UniqueConstraintFound(name, DisplayName(tableSchema, tableName));

                        var uniqueConstraint = new DatabaseUniqueConstraint
                        {
                            Table = table,
                            Name = name
                        };

                        foreach (var columnIndex in record.GetValueOrDefault<short[]>("conkey"))
                        {
                            var constraintColumn = table.Columns[columnIndex - 1];
                            if (constraintColumn == null)
                            {
                                logger.UnsupportedColumnConstraintSkippedWarning(uniqueConstraint.Name, DisplayName(tableSchema, tableName));
                                goto UniqueConstraintEnd;
                            }

                            uniqueConstraint.Columns.Add(constraintColumn);
                        }

                        table.UniqueConstraints.Add(uniqueConstraint);
                        constraintIndexes.Add(record.GetValueOrDefault<uint>("conindid"));

                        UniqueConstraintEnd: ;
                    }
                }
            }
        }

        /// <summary>
        /// Queries the database for defined sequences and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="tables">The database tables.</param>
        /// <param name="schemaFilter">The schema filter.</param>
        /// <param name="logger">The diagnostic logger.</param>
        /// <returns>
        /// A collection of sequences defined in teh database.
        /// </returns>
        [NotNull]
        static IEnumerable<DatabaseSequence> GetSequences(
            [NotNull] NpgsqlConnection connection,
            [NotNull] IList<DatabaseTable> tables,
            [CanBeNull] Func<string, string> schemaFilter,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            var commandText = $@"
SELECT
  sequence_schema,
  sequence_name,
  data_type,
  start_value::bigint,
  minimum_value::bigint,
  maximum_value::bigint,
  increment::int,
  CASE
    WHEN cycle_option = 'YES' THEN TRUE
    ELSE FALSE
  END AS is_cyclic,
  ownerns.nspname AS owner_schema,
  tblcls.relname AS owner_table,
  attname AS owner_column
FROM information_schema.sequences
JOIN pg_namespace AS seqns ON seqns.nspname = sequence_schema
JOIN pg_class AS seqcls ON seqcls.relnamespace = seqns.oid AND seqcls.relname = sequence_name AND seqcls.relkind = 'S'
LEFT OUTER JOIN pg_depend AS dep ON dep.objid = seqcls.oid AND deptype='a'
LEFT OUTER JOIN pg_class AS tblcls ON tblcls.oid = dep.refobjid
LEFT OUTER JOIN pg_attribute AS att ON attrelid = dep.refobjid AND attnum = dep.refobjsubid
LEFT OUTER JOIN pg_namespace AS ownerns ON ownerns.oid = tblcls.relnamespace
{(schemaFilter != null ? $"WHERE {schemaFilter("sequence_schema")}" : null)}";

            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    // If the sequence is OWNED BY a column which is a serial, we skip it. The sequence will be created implicitly.
                    if (!reader.IsDBNull(10))
                    {
                        var ownerSchema = reader.GetValueOrDefault<string>("owner_schema");
                        var ownerTable = reader.GetValueOrDefault<string>("owner_table");
                        var ownerColumn = reader.GetValueOrDefault<string>("owner_column");

                        var ownerDatabaseTable = tables.FirstOrDefault(t => t.Name == ownerTable && t.Schema == ownerSchema);

                        // The sequence is owned by a table that isn't being scaffolded because it was excluded
                        // from the table selection set. Skip the sequence.
                        if (ownerDatabaseTable == null)
                            continue;

                        var ownerDatabaseColumn = ownerDatabaseTable.Columns.FirstOrDefault(t => t.Name == ownerColumn);

                        // Don't reverse-engineer sequences which drive serial columns, these are implicitly
                        // reverse-engineered by the serial column.
                        if (ownerDatabaseColumn?.ValueGenerated == ValueGenerated.OnAdd)
                            continue;
                    }

                    var sequence = new DatabaseSequence
                    {
                        Schema = reader.GetValueOrDefault<string>("sequence_schema"),
                        Name = reader.GetValueOrDefault<string>("sequence_name"),
                        StoreType = reader.GetValueOrDefault<string>("data_type"),
                        StartValue = reader.GetValueOrDefault<long>("start_value"),
                        MinValue = reader.GetValueOrDefault<long>("minimum_value"),
                        MaxValue = reader.GetValueOrDefault<long>("maximum_value"),
                        IncrementBy = reader.GetValueOrDefault<int>("increment"),
                        IsCyclic = reader.GetValueOrDefault<bool>("is_cyclic")
                    };

                    SetSequenceStartMinMax(sequence, connection.PostgreSqlVersion, logger);
                    yield return sequence;
                }
            }
        }

        /// <summary>
        /// Queries the database for defined enums and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="databaseModel">The database model.</param>
        [NotNull]
        static HashSet<string> GetEnums([NotNull] NpgsqlConnection connection, [NotNull] DatabaseModel databaseModel)
        {
            const string commandText = @"
SELECT
  nspname,
  typname,
  array_agg(enumlabel ORDER BY enumsortorder) AS labels
FROM pg_enum
JOIN pg_type ON pg_type.oid = enumtypid
JOIN pg_namespace ON pg_namespace.oid = pg_type.typnamespace
GROUP BY
  nspname,
  typname";

            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                // TODO: just return a collection and make this a static utility method.
                var enums = new HashSet<string>();
                while (reader.Read())
                {
                    var schema = reader.GetValueOrDefault<string>("nspname");
                    var name = reader.GetValueOrDefault<string>("typname");
                    var labels = reader.GetValueOrDefault<string[]>("labels");

                    if (schema == "public")
                        schema = null;

                    PostgresEnum.GetOrAddPostgresEnum(databaseModel, schema, name, labels);
                    enums.Add(name);
                }

                return enums;
            }
        }

        /// <summary>
        /// Queries the installed database extensions and registers them with the model.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="databaseModel">The database model.</param>
        static void GetExtensions([NotNull] NpgsqlConnection connection, [NotNull] DatabaseModel databaseModel)
        {
            const string commandText = "SELECT name, default_version, installed_version FROM pg_available_extensions";
            using (var command = new NpgsqlCommand(commandText, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var name = reader.GetString(reader.GetOrdinal("name"));
                    var _ = reader.GetValueOrDefault<string>("default_version");
                    var installedVersion = reader.GetValueOrDefault<string>("installed_version");

                    if (installedVersion == null)
                        continue;

                    if (name == "plpgsql") // Implicitly installed in all PG databases
                        continue;

                    PostgresExtension.GetOrAddPostgresExtension(databaseModel, name);
                }
            }
        }

        #endregion

        #region Configure default values

        /// <summary>
        /// Configures the default value for a column.
        /// </summary>
        /// <param name="column">The column to configure.</param>
        /// <param name="systemTypeName">The type name of the column.</param>
        static void AdjustDefaults([NotNull] DatabaseColumn column, [NotNull] string systemTypeName)
        {
            var defaultValue = column.DefaultValueSql;
            if (defaultValue == null || defaultValue == "(NULL)")
            {
                column.DefaultValueSql = null;
                return;
            }

            if (column.IsNullable)
                return;

            if (defaultValue == "0")
            {
                if (systemTypeName == "float4" ||
                    systemTypeName == "float8" ||
                    systemTypeName == "int2" ||
                    systemTypeName == "int4" ||
                    systemTypeName == "int8" ||
                    systemTypeName == "money" ||
                    systemTypeName == "numeric")
                {
                    column.DefaultValueSql = null;
                    return;
                }
            }

            if (defaultValue == "0.0" || defaultValue == "'0'::numeric")
            {
                if (systemTypeName == "numeric" ||
                    systemTypeName == "float4" ||
                    systemTypeName == "float8" ||
                    systemTypeName == "money")
                {
                    column.DefaultValueSql = null;
                    return;
                }
            }

            if (systemTypeName == "bool" && defaultValue == "false" ||
                systemTypeName == "date" && defaultValue == "'0001-01-01'::date" ||
                systemTypeName == "timestamp" && defaultValue == "'1900-01-01 00:00:00'::timestamp without time zone" ||
                systemTypeName == "time" && defaultValue == "'00:00:00'::time without time zone" ||
                systemTypeName == "interval" && defaultValue == "'00:00:00'::interval" ||
                systemTypeName == "uuid" && defaultValue == "'00000000-0000-0000-0000-000000000000'::uuid")
            {
                column.DefaultValueSql = null;
            }
        }

        /// <summary>
        /// Sets default values (min, max, start) a <see cref="DatabaseSequence"/>.
        /// </summary>
        /// <param name="sequence">The sequence to configure.</param>
        /// <param name="postgresVersion">The PostgreSQL version to target.</param>
        /// <param name="logger">The diagnostic logger.</param>
        static void SetSequenceStartMinMax(
            [NotNull] DatabaseSequence sequence,
            [NotNull] Version postgresVersion,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            long defaultStart, defaultMin, defaultMax;

            switch (sequence.StoreType)
            {
            case "smallint" when sequence.IncrementBy > 0:
                defaultMin = 1;
                defaultMax = short.MaxValue;
                defaultStart = sequence.MinValue ?? 0;
                break;

            case "smallint":
                // PostgreSQL 10 changed the default minvalue for a descending sequence, see #264
                defaultMin = postgresVersion >= new Version(10, 0)
                    ? short.MinValue
                    : short.MinValue + 1;
                defaultMax = -1;
                defaultStart = sequence.MaxValue ?? 0;
                break;

            case "integer" when sequence.IncrementBy > 0:
                defaultMin = 1;
                defaultMax = int.MaxValue;
                defaultStart = sequence.MinValue ?? 0;
                break;

            case "integer":
                // PostgreSQL 10 changed the default minvalue for a descending sequence, see #264
                defaultMin = postgresVersion >= new Version(10, 0)
                    ? int.MinValue
                    : int.MinValue + 1;
                defaultMax = -1;
                defaultStart = sequence.MaxValue ?? 0;
                break;

            case "bigint" when sequence.IncrementBy > 0:
                defaultMin = 1;
                defaultMax = long.MaxValue;
                defaultStart = sequence.MinValue ?? 0;
                break;

            case "bigint":
                // PostgreSQL 10 changed the default minvalue for a descending sequence, see #264
                defaultMin = postgresVersion >= new Version(10, 0)
                    ? long.MinValue
                    : long.MinValue + 1;
                defaultMax = -1;
                Debug.Assert(sequence.MaxValue.HasValue);
                defaultStart = sequence.MaxValue.Value;
                break;

            default:
                logger.Logger.LogWarning($"Sequence with datatype {sequence.StoreType} which isn't an expected sequence type.");
                return;
            }

            if (sequence.StartValue == defaultStart)
                sequence.StartValue = null;

            if (sequence.MinValue == defaultMin)
                sequence.MinValue = null;

            if (sequence.MaxValue == defaultMax)
                sequence.MaxValue = null;
        }

        #endregion

        #region Filter fragment generators

        /// <summary>
        /// Builds a delegate to generate a schema filter fragment.
        /// </summary>
        /// <param name="schemas">The list of schema names.</param>
        /// <returns>
        /// A delegate that generates a schema filter fragment.
        /// </returns>
        [CanBeNull]
        static Func<string, string> GenerateSchemaFilter([NotNull] IReadOnlyList<string> schemas)
            => schemas.Any()
                ? s => $"{s} IN ({string.Join(", ", schemas.Select(EscapeLiteral))})"
                : (Func<string, string>)null;

        /// <summary>
        /// Builds a delegate to generate a table filter fragment.
        /// </summary>
        /// <param name="tables">The list of tables parsed into tuples of schema name and table name.</param>
        /// <param name="schemaFilter">The delegate that generates a schema filter fragment.</param>
        /// <returns>
        /// A delegate that generates a table filter fragment.
        /// </returns>
        [CanBeNull]
        static Func<string, string, string> GenerateTableFilter(
            [NotNull] IReadOnlyList<(string Schema, string Table)> tables,
            [CanBeNull] Func<string, string> schemaFilter)
            => schemaFilter != null || tables.Any()
                ? (s, t) =>
                {
                    var tableFilterBuilder = new StringBuilder();

                    var openBracket = false;
                    if (schemaFilter != null)
                    {
                        tableFilterBuilder
                            .Append("(")
                            .Append(schemaFilter(s));
                        openBracket = true;
                    }

                    if (tables.Any())
                    {
                        if (openBracket)
                        {
                            tableFilterBuilder
                                .AppendLine()
                                .Append("OR ");
                        }
                        else
                        {
                            tableFilterBuilder.Append("(");
                            openBracket = true;
                        }

                        var tablesWithoutSchema = tables.Where(e => string.IsNullOrEmpty(e.Schema)).ToList();
                        if (tablesWithoutSchema.Any())
                        {
                            tableFilterBuilder.Append(t);
                            tableFilterBuilder.Append(" IN (");
                            tableFilterBuilder.Append(string.Join(", ", tablesWithoutSchema.Select(e => EscapeLiteral(e.Table))));
                            tableFilterBuilder.Append(")");
                        }

                        var tablesWithSchema = tables.Where(e => !string.IsNullOrEmpty(e.Schema)).ToList();
                        if (tablesWithSchema.Any())
                        {
                            if (tablesWithoutSchema.Any())
                                tableFilterBuilder.Append(" OR ");

                            tableFilterBuilder.Append(t);
                            tableFilterBuilder.Append(" IN (");
                            tableFilterBuilder.Append(string.Join(", ", tablesWithSchema.Select(e => EscapeLiteral(e.Table))));
                            tableFilterBuilder.Append(") AND (");
                            tableFilterBuilder.Append(s);
                            tableFilterBuilder.Append(" || '.' || ");
                            tableFilterBuilder.Append(t);
                            tableFilterBuilder.Append(") IN (");
                            tableFilterBuilder.Append(string.Join(", ", tablesWithSchema.Select(e => EscapeLiteral($"{e.Schema}.{e.Table}"))));
                            tableFilterBuilder.Append(")");
                        }
                    }

                    if (openBracket)
                        tableFilterBuilder.Append(")");

                    return tableFilterBuilder.ToString();
                }
                : (Func<string, string, string>)null;

        #endregion

        #region Utilities

        /// <summary>
        /// Type names as returned by PostgreSQL's format_type need to be cleaned up a bit
        /// </summary>
        /// <param name="formattedTypeName">The type name to adjust.</param>
        /// <returns>
        /// The adjusted type name or the original name if no adjustments were required.
        /// </returns>
        [NotNull]
        static string AdjustFormattedTypeName([NotNull] string formattedTypeName)
        {
            // User-defined types (e.g. enums) with capital letters get formatted with quotes, remove.
            if (formattedTypeName[0] == '"')
                formattedTypeName = formattedTypeName.Substring(1, formattedTypeName.Length - 2);

            if (formattedTypeName == "bpchar")
                formattedTypeName = "char";

            return formattedTypeName;
        }

        /// <summary>
        /// Maps a character to a <see cref="ReferentialAction"/>.
        /// </summary>
        /// <param name="onDeleteAction">The character to map.</param>
        /// <returns>
        /// A <see cref="ReferentialAction"/> associated with the <paramref name="onDeleteAction"/> character.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Unknown value <paramref name="onDeleteAction"/> for foreign key deletion action code.
        /// </exception>
        static ReferentialAction ConvertToReferentialAction(char onDeleteAction)
        {
            switch (onDeleteAction)
            {
            case 'a':
                return ReferentialAction.NoAction;
            case 'r':
                return ReferentialAction.Restrict;
            case 'c':
                return ReferentialAction.Cascade;
            case 'n':
                return ReferentialAction.SetNull;
            case 'd':
                return ReferentialAction.SetDefault;
            default:
                throw new ArgumentOutOfRangeException($"Unknown value {onDeleteAction} for foreign key deletion action code.");
            }
        }

        /// <summary>
        /// Constructs the display name given a schema and table name.
        /// </summary>
        /// <param name="schema">The schema name.</param>
        /// <param name="name">The table name.</param>
        /// <returns>
        /// A display name in the form of 'schema.name' or 'name'.
        /// </returns>
        // TODO: should this default to/screen out the public schema?
        [NotNull]
        static string DisplayName([CanBeNull] string schema, [NotNull] string name)
            => string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";

        /// <summary>
        /// Parses the table name into a tuple of schema name and table name where the schema may be null.
        /// </summary>
        /// <param name="table">The name to parse.</param>
        /// <returns>
        /// A tuple of schema name and table name where the schema may be null.
        /// </returns>
        /// <exception cref="InvalidOperationException">The table name could not be parsed.</exception>
        static (string Schema, string Table) ParseSchemaTable([NotNull] string table)
        {
            var match = SchemaTableNameExtractor.Match(table.Trim());

            if (!match.Success)
                throw new InvalidOperationException("The table name could not be parsed.");

            var part1 = match.Groups["part1"].Value;
            var part2 = match.Groups["part2"].Value;

            return string.IsNullOrEmpty(part2) ? (null, part1) : (part1, part2);
        }

        /// <summary>
        /// Wraps a string literal in single quotes.
        /// </summary>
        /// <param name="s">The string literal.</param>
        /// <returns>
        /// The string literal wrapped in single quotes.
        /// </returns>
        [NotNull]
        static string EscapeLiteral([CanBeNull] string s) => $"'{s}'";

        #endregion
    }
}
