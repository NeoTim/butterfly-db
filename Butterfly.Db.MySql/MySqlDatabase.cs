﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using MySql.Data.MySqlClient;
using NLog;

using Butterfly.Db;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Db.MySql {

    /// <inheritdoc/>
    public class MySqlDatabase : BaseDatabase {

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public MySqlDatabase(string connectionString) : base(connectionString) {
        }

        public override bool CanJoin => true;

        public override bool CanFieldAlias => true;

        protected override async Task LoadSchemaAsync() {
            try {
                string commandText = "show tables";
                using (MySqlDataReader reader = await ExecuteReaderAsync(this.ConnectionString, commandText)) {
                    while (reader.Read()) {
                        string tableName = reader[0].ToString();
                        Table table = await this.LoadTableSchemaAsync(tableName);
                        this.tableByName[tableName] = table;
                    }
                }
            }
            catch (MySqlException e) {
                if (e.Message.StartsWith("Unable to connect")) {
                    throw new UnableToConnectDatabaseException(e.Message);
                }
                else {
                    throw;
                }
            }
        }

        protected override async Task<Table> LoadTableSchemaAsync(string tableName) {
            TableFieldDef[] fieldDefs = await this.GetFieldDefs(tableName);
            TableIndex[] uniqueIndexes = this.GetUniqueIndexes(tableName);
            return new Table(tableName, fieldDefs, uniqueIndexes);
        }

        protected async Task<TableFieldDef[]> GetFieldDefs(string tableName) {
            List<TableFieldDef> fields = new List<TableFieldDef>();
            string commandText = $"DESCRIBE {tableName}";
            using (MySqlDataReader reader = await ExecuteReaderAsync(this.ConnectionString, commandText)) {
                while (reader.Read()) {
                    string name = reader[0].ToString();
                    string typeText = reader[1].ToString();
                    string allowNullText = reader[2].ToString();
                    string key = reader[3].ToString();
                    string extra = reader[5].ToString();

                    (Type type, int maxLength) = ConvertMySqlType(typeText);
                    bool allowNull = allowNullText.Equals("YES", StringComparison.OrdinalIgnoreCase);
                    bool isAutoIncrement = !string.IsNullOrEmpty(extra) && extra.Contains("auto_increment");
                    fields.Add(new TableFieldDef(name, type, maxLength, allowNull, isAutoIncrement));
                }
            }
            return fields.ToArray();
        }

        protected TableIndex[] GetUniqueIndexes(string tableName) {
            List<TableIndex> uniqueIndexes = new List<TableIndex>();
            string commandText = $"SHOW INDEX FROM {tableName}";
            TableIndexType lastTableIndexType = TableIndexType.Other;
            string lastIndexName = null;
            List<string> lastFieldNames = new List<string>();
            using (MySqlDataReader reader = ExecuteReader(this.ConnectionString, commandText)) {
                while (reader.Read()) {
                    bool unique = int.Parse(reader[1].ToString()) == 0;
                    string indexName = reader[2].ToString();
                    string columnName = reader[4].ToString();

                    if (indexName != lastIndexName) {
                        if (lastFieldNames.Count > 0) {
                            uniqueIndexes.Add(new TableIndex(lastTableIndexType, lastFieldNames.ToArray()));
                        }
                        if (indexName=="PRIMARY") {
                            lastTableIndexType = TableIndexType.Primary;
                        }
                        else if (unique) {
                            lastTableIndexType = TableIndexType.Unique;
                        }
                        else {
                            lastTableIndexType = TableIndexType.Other;
                        }
                        lastIndexName = indexName;
                        lastFieldNames.Clear();
                    }
                    lastFieldNames.Add(columnName);
                }
                if (lastFieldNames.Count > 0) {
                    TableIndex uniqueIndex = new TableIndex(lastTableIndexType, lastFieldNames.ToArray());
                    uniqueIndexes.Add(uniqueIndex);
                }
            }
            return uniqueIndexes.ToArray();
        }

        protected override BaseTransaction CreateTransaction() {
            return new MySqlTransaction(this);
        }

        protected override async Task<Dict[]> DoSelectRowsAsync(string executableSql, Dict executableParams, int limit) {
            SelectStatement statement = new SelectStatement(this, executableSql);
            MySqlParameter[] mySqlParams = executableParams.Select(keyValuePair => new MySqlParameter(keyValuePair.Key, keyValuePair.Value)).ToArray();

            List<Dict> rows = new List<Dict>();
            try {
                var sql = limit > 0 ? $"{executableSql} LIMIT {limit}" : executableSql;
                using (MySqlDataReader reader = await ExecuteReaderAsync(this.ConnectionString, sql, mySqlParams)) {
                    ReadOnlyCollection<DbColumn> columns = null;
                    while (reader.Read()) {
                        if (columns==null) columns = reader.GetColumnSchema();
                        Dict row = new Dictionary<string, object>();
                        foreach (var column in columns) {
                            row[column.ColumnName] = ConvertValue(reader[column.ColumnName]);
                        }
                        rows.Add(row);
                    }
                }
            }
            catch (Exception e) {
                logger.Error(e, $"Error executing {statement.Sql}...");
                throw;
            }

            return rows.ToArray();
        }

        protected override async Task<Dict[]> DoQueryRowsAsync(string storedProcedureName, Dict executableParams) {
            MySqlParameter[] mySqlParams = executableParams.Select(keyValuePair => new MySqlParameter(keyValuePair.Key, keyValuePair.Value)).ToArray();

            List<Dict> rows = new List<Dict>();
            try {
                using (MySqlDataReader reader = await ExecuteReaderAsync(this.ConnectionString, storedProcedureName, mySqlParams, commandType: CommandType.StoredProcedure)) {
                    while (reader.Read()) {
                        Dict row = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++) {
                            row[reader.GetName(i)] = ConvertValue(reader[i]);
                        }
                        rows.Add(row);
                    }
                }
            }
            catch (Exception e) {
                logger.Error(e, $"Error executing {storedProcedureName}...");
                throw;
            }

            return rows.ToArray();
        }

        protected static object ConvertValue(object value) {
            if (value == null || value == DBNull.Value) {
                return null;
            }
            else {
                return value;
            }
        }

        private static MySqlDataReader ExecuteReader(string connectionString, string commandText) {
            MySqlConnection connection = new MySqlConnection(connectionString);
            connection.Open();
            using (MySqlCommand command = new MySqlCommand(commandText, connection)) {
                return command.ExecuteReader(CommandBehavior.CloseConnection);
            }
        }

        private static async Task<MySqlDataReader> ExecuteReaderAsync(string connectionString, string commandText, MySqlParameter[] parameters = null, CommandType commandType = CommandType.Text) {
            MySqlConnection connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            using (MySqlCommand command = new MySqlCommand(commandText, connection)) {
                command.CommandType = commandType;
                if (parameters!=null) command.Parameters.AddRange(parameters);
                return (MySqlDataReader) await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);
            }
        }

        protected readonly static Regex PARSE_TYPE = new Regex(@"^(?<type>.+?)(?<maxLengthWithParens>\(\d+\))?$");

        public static (Type, int) ConvertMySqlType(string text) {
            Match match = PARSE_TYPE.Match(text);
            if (!match.Success) throw new Exception($"Could not parse SQL type '{text}'");

            string typeText = match.Groups["type"].Value;

            Type type;
            if (typeText.EndsWith("CHAR", StringComparison.OrdinalIgnoreCase) || typeText.EndsWith("TEXT", StringComparison.OrdinalIgnoreCase) || typeText.EndsWith("JSON", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(string);
            }
            else if (typeText.Equals("TINYINT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(byte);
            }
            else if (typeText.Equals("MEDIUMINT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(int);
            }
            else if (typeText.Equals("INT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(long);
            }
            else if (typeText.Equals("BIGINT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(long);
            }
            else if (typeText.Equals("FLOAT", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(float);
            }
            else if (typeText.Equals("DOUBLE", StringComparison.OrdinalIgnoreCase)) {
                type = typeof(double);
            }
            else if ((typeText.Equals("DATETIME", StringComparison.OrdinalIgnoreCase)) ||
                                         (typeText.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase))) {
                type = typeof(DateTime);
            }
            else {
                throw new Exception($"Unknown field type '{text}'");
            }

            string maxLengthText = match.Groups["maxLengthWithParens"].Value.Replace("(", "").Replace(")", "");
            if (!int.TryParse(maxLengthText, out int maxLength)) maxLength = -1;

            return (type, maxLength);
        }

    }
}
