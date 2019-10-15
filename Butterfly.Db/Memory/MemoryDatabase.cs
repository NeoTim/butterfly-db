﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Butterfly.Util;
using NLog;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Db.Memory {

    /// <inheritdoc/>
    public class MemoryDatabase : BaseDatabase {

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public MemoryDatabase() : base(null) {
        }

        /// <summary>
        /// Creates database tables from an embedded resource file by internally calling <see cref="CreateFromSqlAsync(string)"/> with the contents of the embedded resource file"
        /// </summary>
        /// <param name="assembly"></param>
        /// <param name="resourceFile"></param>
        /// <returns></returns>
        public async Task CreateFromResourceFileAsync(Assembly assembly, string resourceFile) {
            //logger.Debug($"CreateFromResourceFileAsync():resourceNames={string.Join(",", assembly.GetManifestResourceNames())}");
            string sql = await FileX.LoadResourceAsTextAsync(assembly, resourceFile);
            await this.CreateFromSqlAsync(sql);
        }

        /// <summary>
        /// Creates database tables from a string containing a semicolon delimited series of CREATE statements in MySQL format (will be converted to native database format as appropriate).<para/>
        /// Lines beginning with <code>--</code> will be ignored. Each CREATE statement must include a PRIMARY KEY definition. If the table already exists, the CREATE statement is ignored.<para/>
        /// Creating database tables with this method is not required (primarily done as a convenience method for unit testing)"/>.
        /// </summary>
        /// <param name="sql"></param>
        public async Task CreateFromSqlAsync(string createStatements) {
            logger.Trace($"CreateFromTextAsync():createStatements={createStatements}");
            var noCommentSql = SQL_COMMENT.Replace(createStatements, "");
            var sqlParts = noCommentSql.Split(';').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x));

            List<string> tableSchemasToLoad = new List<string>();
            using (var transaction = await this.BeginTransactionAsync() as BaseTransaction) {
                foreach (var sqlPart in sqlParts) {
                    if (!string.IsNullOrWhiteSpace(sqlPart)) {
                        CreateStatement statement = this.CreateStatement(sqlPart);
                        if (!this.TableByName.Keys.Contains(statement.TableName)) {
                            bool tableSchemaLoaded = await transaction.CreateAsync(statement);
                            if (!tableSchemaLoaded) {
                                tableSchemasToLoad.Add(statement.TableName);
                            }
                        }
                    }
                }
                await transaction.CommitAsync();
            }

            foreach (var tableName in tableSchemasToLoad) {
                Table table = await this.LoadTableSchemaAsync(tableName);
                this.tableByName[table.Name] = table;
            }
        }

        protected virtual CreateStatement CreateStatement(string sql) {
            return new CreateStatement(sql);
        }

        protected override Task LoadSchemaAsync() {
            return Task.FromResult(0);
        }

        protected override Task<Table> LoadTableSchemaAsync(string tableName) {
            return null;
        }

        protected override BaseTransaction CreateTransaction() {
            return new MemoryTransaction(this);
        }

        protected override Task<Dict[]> DoSelectRowsAsync(string executableSql, Dict executableParams, int limit) {
            SelectStatement executableStatement = new SelectStatement(this, executableSql);
            if (executableStatement.StatementFromRefs.Length > 1) throw new Exception("MemoryTable does not support joins");
            if (!(executableStatement.StatementFromRefs[0].table is MemoryTable memoryTable)) throw new Exception("Table is not a MemoryTable");

            string[] fieldNames = string.IsNullOrEmpty(executableStatement.selectClause) || executableStatement.selectClause=="*" ? memoryTable.DataTable.Columns.Cast<DataColumn>().Select(x => x.ColumnName).ToArray() : executableStatement.selectClause.Split(',').Select(x => x.Trim()).ToArray();
            if (fieldNames.Any(x => x.Contains(' '))) throw new Exception("MemoryTable does not support field aliases");

            string evaluatedWhereClause = EvaluateWhereClause(executableStatement.whereClause, executableParams, executableStatement.StatementFromRefs);
            DataRow[] dataRows = memoryTable.DataTable.Select(evaluatedWhereClause, executableStatement.orderByClause, DataViewRowState.OriginalRows);
            List<Dict> rows = new List<Dict>();
            foreach (var dataRow in dataRows) {
                Dict row = new Dict();
                foreach (var fieldName in fieldNames) {
                    row[fieldName] = dataRow[fieldName, DataRowVersion.Original];
                }
                rows.Add(row);
                if (limit > 0 && rows.Count >= limit) break;
            }
            return Task.FromResult(rows.ToArray());
        }

        protected override Task<Dict[]> DoQueryRowsAsync(string storedProcedureName, Dict vars = null) {
            throw new NotImplementedException();
        }

        protected static readonly Regex SIMPLE_REPLACE = new Regex(@"(?<tableAliasWithDot>\w+\.)?(?<fieldName>\w+)\s*(?<op>=|<>|!=|>|<)\s*(?<param>\@\w+)");
        protected static readonly Regex IN_REPLACE = new Regex(@"(?<tableAliasWithDot>\w+\.)?(?<fieldName>\w+)\s+(?<op>IN|NOT\s+IN)\s+\((?<param>[^\)]+)\)", RegexOptions.IgnoreCase);

        public override bool CanJoin => false;
        public override bool CanFieldAlias => false;

        public static string EvaluateWhereClause(string whereClause, Dict sqlParams, StatementFromRef[] tableRefs) {
            string newWhereClause = whereClause;
            newWhereClause = EvaluateWhereClauseReplace(newWhereClause, SIMPLE_REPLACE, sqlParams, tableRefs, op => {
                if (op == "!=") return "<>";
                else return op;
            });
            newWhereClause = EvaluateWhereClauseReplace(newWhereClause, IN_REPLACE, sqlParams, tableRefs);
            return newWhereClause;
        }

        protected static string EvaluateWhereClauseReplace(string sql, Regex regex, Dict sqlParams, StatementFromRef[] tableRefs, Func<string, string> remapOp = null) {
            if (string.IsNullOrEmpty(sql)) return null;

            StringBuilder sb = new StringBuilder();
            int lastIndex = 0;
            foreach (Match match in regex.Matches(sql)) {
                sb.Append(sql.Substring(lastIndex, match.Groups["op"].Index - lastIndex));
                string op = match.Groups["op"].Value;
                sb.Append(remapOp !=null ? remapOp(op) : op);
                lastIndex = match.Groups["op"].Index + match.Groups["op"].Length;

                sb.Append(sql.Substring(lastIndex, match.Groups["param"].Index - lastIndex));

                // Get table ref
                string tableAlias = match.Groups["tableAliasWithDot"].Value.Replace(".", "");
                StatementFromRef tableRef;
                if (string.IsNullOrEmpty(tableAlias)) {
                    if (tableRefs.Length > 1) throw new Exception("SELECT statements with more than one table reference must use table aliases for all where clause fields");
                    tableRef = tableRefs[0];
                }
                else {
                    tableRef = Array.Find(tableRefs, x => x.tableAlias==tableAlias);
                }

                // Get field defs
                string fieldName = match.Groups["fieldName"].Value;
                if (fieldName.Equals("NOT", StringComparison.OrdinalIgnoreCase)) {
                    lastIndex = match.Groups["param"].Index;
                }
                else {
                    TableFieldDef fieldDef = tableRef.table.FindFieldDef(fieldName);

                    // Get evaluated value
                    var paramNames = match.Groups["param"].Value.Split(',').Select(x => x.Replace("@", "").Trim());
                    bool isFirst = true;
                    foreach (var paramName in paramNames) {
                        object replacementValue = sqlParams[paramName];
                        string evaluatedValue;
                        if (fieldDef.type == typeof(string)) {
                            evaluatedValue = $"'{replacementValue}'";
                        }
                        else if (fieldDef.type == typeof(DateTime)) {
                            evaluatedValue = $"#{replacementValue}#";
                        }
                        else {
                            evaluatedValue = $"{replacementValue}";
                        }
                        if (isFirst) isFirst = false;
                        else sb.Append(',');
                        sb.Append(evaluatedValue);
                    }
                    lastIndex = match.Groups["param"].Index + match.Groups["param"].Length;
                }
            }
            sb.Append(sql.Substring(lastIndex));
            return sb.ToString();
        }
    }
}
