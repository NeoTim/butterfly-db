﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using NLog;

using Butterfly.Db;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Db.SQLite {

    /// <inheritdoc/>
    public class SQLiteDatabase : BaseDatabase {

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public override bool CanJoin => true;

        public override bool CanFieldAlias => true;

        public SQLiteDatabase(string connectionString) : base(connectionString) {
        }

        protected override async Task LoadSchemaAsync() {
            string commandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using (var connection = new SqliteConnection(this.ConnectionString)) {
                connection.Open();
                var command = new SqliteCommand(commandText, connection);
                using (var reader = command.ExecuteReader()) {
                    while (reader.Read()) {
                        string tableName = reader[0].ToString();
                        Table table = await this.LoadTableSchemaAsync(tableName);
                        this.tableByName[table.Name] = table;
                    }
                }
            }
        }

        protected override async Task<Table> LoadTableSchemaAsync(string tableName) {
            TableFieldDef[] fieldDefs = await this.GetFieldDefs(tableName);
            TableIndex[] indexes = await this.GetIndexes(tableName, fieldDefs);
            return new Table(tableName, fieldDefs, indexes);
        }

        protected async Task<TableFieldDef[]> GetFieldDefs(string tableName) {
            List<TableFieldDef> fieldDefs = new List<TableFieldDef>();
            string commandText = $"pragma table_info({tableName})";
            using (var connection = new SqliteConnection(this.ConnectionString)) {
                await connection.OpenAsync();
                var command = new SqliteCommand(commandText, connection);
                using (var reader = command.ExecuteReader()) {
                    while (await reader.ReadAsync()) {
                        var fieldName = ConvertValue(reader[1])?.ToString();
                        var fieldType = ConvertValue(reader[2])?.ToString();
                        Type dataType;
                        if (fieldType=="TEXT") {
                            dataType = typeof(string);
                        }
                        else if (fieldType=="INTEGER") {
                            dataType = typeof(int);
                        }
                        else if (fieldType == "REAL") {
                            dataType = typeof(float);
                        }
                        else {
                            throw new Exception($"Unknown field type '{fieldType}'");
                        }
                        var notNull = ConvertValue(reader[3])?.ToString() == "1";
                        var isPrimaryKey = ConvertValue(reader[5])?.ToString() == "1";

                        var fieldDef = new TableFieldDef(fieldName, dataType, -1, !notNull, isPrimaryKey && dataType==typeof(int));
                        fieldDefs.Add(fieldDef);
                    }
                }
            }
            return fieldDefs.ToArray();
        }

        protected async Task<TableIndex[]> GetIndexes(string tableName, TableFieldDef[] fieldDefs) {
            List<TableIndex> tableIndexes = new List<TableIndex>();
            string commandText = $"PRAGMA index_list({tableName});";
            using (var connection = new SqliteConnection(this.ConnectionString)) {
                await connection.OpenAsync();
                var command = new SqliteCommand(commandText, connection);
                using (var reader = command.ExecuteReader()) {
                    while (await reader.ReadAsync()) {
                        string indexName = null;
                        TableIndexType indexType = TableIndexType.Other;
                        for (int i = 0; i < reader.FieldCount; i++) {
                            var name = reader.GetName(i);
                            var value = ConvertValue(reader[i])?.ToString();
                            if (name == "name") indexName = value;
                            else if (name == "unique" && value == "1") indexType = TableIndexType.Unique;
                            else if (name == "origin" && value == "pk") indexType = TableIndexType.Primary;
                        }
                        if (!string.IsNullOrEmpty(indexName)) {
                            var fieldNames = await GetIndexFieldNames(indexName);
                            var tableIndex = new TableIndex(indexType, fieldNames);
                            tableIndexes.Add(tableIndex);
                        }
                    }
                }
            }

            // Not sure why auto increment fields don't have an index created in PRAGMA results
            foreach (var autoIncrementFieldDef in fieldDefs.Where(x => x.isAutoIncrement)) {
                tableIndexes.Add(new TableIndex(TableIndexType.Primary, new string[] { autoIncrementFieldDef.name }));
            }

            return tableIndexes.ToArray();
        }

        protected async Task<string[]> GetIndexFieldNames(string indexName) {
            List<string> fieldNames = new List<string>();
            string commandText = $"PRAGMA index_info({indexName});";
            using (var connection = new SqliteConnection(this.ConnectionString)) {
                await connection.OpenAsync();
                var command = new SqliteCommand(commandText, connection);
                using (var reader = command.ExecuteReader()) {
                    while (await reader.ReadAsync()) {
                        var fieldName = ConvertValue(reader[2])?.ToString();
                        fieldNames.Add(fieldName);
                    }
                }
                return fieldNames.ToArray();
            }
        }

        protected override BaseTransaction CreateTransaction() {
            return new SQLiteTransaction(this);
        }

        protected override async Task<Dict[]> DoSelectRowsAsync(string executableSql, Dict executableParams, int limit) {
            SelectStatement statement = new SelectStatement(this, executableSql);

            List<Dict> rows = new List<Dict>();
            try {
                using (var connection = new SqliteConnection(this.ConnectionString)) {
                    await connection.OpenAsync();
                    var sql = limit > 0 ? $"{executableSql} LIMIT {limit}" : executableSql;
                    var command = new SqliteCommand(sql, connection);
                    foreach (var keyValuePair in executableParams) {
                        command.Parameters.AddWithValue(keyValuePair.Key, keyValuePair.Value);
                    }
                    using (var reader = await command.ExecuteReaderAsync()) {
                        while (await reader.ReadAsync()) {
                            Dict row = new Dictionary<string, object>();
                            for (int i=0; i<reader.FieldCount; i++) {
                                var name = reader.GetName(i);
                                var value = ConvertValue(reader[i]);
                                row[name] = value;
                            }
                            rows.Add(row);
                        }
                    }
                }
            }
            catch (Exception e) {
                logger.Error(e, $"Error executing {statement.Sql}...");
                throw;
            }

            return rows.ToArray();
        }

        protected override Task<Dict[]> DoQueryRowsAsync(string storedProcedureName, Dict vars = null) {
            throw new NotImplementedException();
        }

        protected static object ConvertValue(object value) {
            if (value == null || value == DBNull.Value) {
                return null;
            }
            else {
                return value;
            }
        }

    }
}
