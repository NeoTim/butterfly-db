﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Butterfly.Db.Event;
using Butterfly.Util;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Db {

    /// <inheritdoc/>
    /// <summary>
    /// Base class implementing <see cref="ITransaction"/>.
    /// </summary>
    public abstract class BaseTransaction : ITransaction {
        protected readonly BaseDatabase database;

        protected readonly List<KeyValueDataEvent> dataEvents = new List<KeyValueDataEvent>();
        protected readonly List<OnCommitRef> onCommitRefs = new List<OnCommitRef>();

        public BaseTransaction(BaseDatabase database) {
            this.database = database;
            this.database.TransactionCount++;
        }

        public IDatabase Database => this.database;

        // Create methods
        public bool Create(CreateStatement statement) {
            return this.DoCreate(statement);
        }

        protected abstract bool DoCreate(CreateStatement statement);

        public async Task<bool> CreateAsync(CreateStatement statement) {
            return await this.DoCreateAsync(statement);
        }

        protected abstract Task<bool> DoCreateAsync(CreateStatement statement);

        // Insert methods
        public async Task<T> InsertAsync<T>(string insertStatement, dynamic vars, bool ignoreIfDuplicate = false) {
            InsertStatement statement = new InsertStatement(this.database, insertStatement);
            object result = await this.InsertAsync(statement, vars, ignoreIfDuplicate: ignoreIfDuplicate);
            return (T)Convert.ChangeType(result, typeof(T));
        }

        public async Task<object> InsertAsync(InsertStatement insertStatement, dynamic vars, bool ignoreIfDuplicate = false) {
            // Convert statementParams
            Dict varsDict = insertStatement.ConvertParamsToDict(vars);
            this.database.PreprocessInput(insertStatement.StatementFromRefs[0].table.Name, varsDict);
            Dict varsOverrides = this.database.GetOverrideValues(insertStatement.StatementFromRefs[0].table);
            varsDict.UpdateFrom(varsOverrides);
            Dict varsDefaults = this.database.GetDefaultValues(insertStatement.StatementFromRefs[0].table);

            // Get the executable sql and params
            (string executableSql, Dict executableParams) = insertStatement.GetExecutableSqlAndParams(varsDict, varsDefaults);

            // Execute insert and return getGenerateId lambda
            Func<object> getGeneratedId;
            try {
                getGeneratedId = await this.DoInsertAsync(executableSql, executableParams, ignoreIfDuplicate);
            }
            catch (DuplicateKeyDatabaseException) {
                if (ignoreIfDuplicate) return null;
                throw;
            }

            // Determine keyValue (either keyValue is from a generated id or was included in the statement params)
            object keyValue;
            if (insertStatement.StatementFromRefs[0].table.AutoIncrementFieldName != null && getGeneratedId != null) {
                keyValue = getGeneratedId();
            }
            else {
                keyValue = executableParams.GetKeyValue(insertStatement.StatementFromRefs[0].table.Indexes[0].FieldNames);
            }

            // Create data event
            this.dataEvents.Add(new KeyValueDataEvent(DataEventType.Insert, insertStatement.StatementFromRefs[0].table.Name, keyValue));

            this.database.InsertCount++;

            return keyValue;
        }

        protected abstract Task<Func<object>> DoInsertAsync(string executableSql, Dict executableParams, bool ignoreIfDuplicate);

        // Update methods
        public async Task<int> UpdateAsync(string updateStatement, dynamic vars) {
            UpdateStatement statement = new UpdateStatement(this.database, updateStatement);
            return await this.UpdateAsync(statement, vars);
        }

        public async Task<int> UpdateAsync(UpdateStatement updateStatement, dynamic vars) {
            Dict varsDict = updateStatement.ConvertParamsToDict(vars);
            Dict varsOverrides = this.database.GetOverrideValues(updateStatement.StatementFromRefs[0].table);
            varsDict.UpdateFrom(varsOverrides);
            this.database.PreprocessInput(updateStatement.StatementFromRefs[0].table.Name, varsDict);

            (var whereIndex, var setRefs, var whereRefs) = updateStatement.GetWhereIndexSetRefsAndWhereRefs(this.database, varsDict);
            (string executableSql, Dict executableParams) = updateStatement.GetExecutableSqlAndParams(varsDict, setRefs, whereRefs);

            object keyValue = await this.GetKeyValue(whereIndex, varsDict, executableParams, whereRefs, updateStatement.StatementFromRefs[0].table.Indexes[0], updateStatement.StatementFromRefs[0].table.Name);

            int count;
            if (keyValue == null) {
                count = 0;
            }
            else {
                count = await this.DoUpdateAsync(executableSql, executableParams);

                this.dataEvents.Add(new KeyValueDataEvent(DataEventType.Update, updateStatement.StatementFromRefs[0].table.Name, keyValue));

                this.database.UpdateCount++;
            }

            return count;
        }

        protected abstract Task<int> DoUpdateAsync(string executableSql, Dict executableParams);

        // Delete methods
        public async Task<int> DeleteAsync(string deleteStatement, dynamic vars) {
            DeleteStatement statement = new DeleteStatement(this.database, deleteStatement);
            return await this.DeleteAsync(statement, vars);
        }

        public async Task<int> DeleteAsync(DeleteStatement deleteStatement, dynamic vars) {
            Dict varsDict = deleteStatement.ConvertParamsToDict(vars);

            (var whereIndex, var whereRefs) = deleteStatement.GetWhereIndexAndWhereRefs(this.database, varsDict);

            (string executableSql, Dict executableParams) = deleteStatement.GetExecutableSqlAndParams(varsDict, whereRefs);

            var tableName = deleteStatement.StatementFromRefs[0].table.Name;
            var primaryIndex = deleteStatement.StatementFromRefs[0].table.Indexes[0];
            object keyValue = await this.GetKeyValue(whereIndex, varsDict, executableParams, whereRefs, primaryIndex, tableName);

            Dict record = null;
            var deleteForeignKeys = this.database.GetForeignKeys(tableName, ForeignKeyRule.DeleteChildOnParentDelete, ForeignKeyRule.NullChildOnParentDelete);
            if (deleteForeignKeys.Length>0) {
                record = await this.database.SelectRowAsync(tableName, keyValue);
            }

            int count;
            if (keyValue == null) {
                count = 0;
            }
            else {
                count = await this.DoDeleteAsync(executableSql, executableParams);

                if (count > 0) {
                    this.dataEvents.Add(new KeyValueDataEvent(DataEventType.Delete, tableName, keyValue));

                    foreach (var foreignKey in deleteForeignKeys) {
                        var childSelectValues = new Dict();
                        for (int i=0; i<foreignKey.parentFieldNames.Length; i++) {
                            childSelectValues[foreignKey.childFieldNames[i]] = record[foreignKey.parentFieldNames[i]];
                        }
                        var childRecords = await this.database.SelectRowsAsync(foreignKey.childTable.Name, childSelectValues);
                        foreach (var childRecord in childRecords) {
                            Dict modifyValues = new Dict();
                            foreach (var childPrimaryKeyFieldName in foreignKey.childTable.Indexes[0].FieldNames) {
                                modifyValues[childPrimaryKeyFieldName] = childRecord[childPrimaryKeyFieldName];
                            }
                            switch (foreignKey.foreignKeyRule) {
                                case ForeignKeyRule.DeleteChildOnParentDelete:
                                    await this.DeleteAsync(foreignKey.childTable.Name, modifyValues);
                                    break;
                                case ForeignKeyRule.NullChildOnParentDelete:
                                    for (int i = 0; i < foreignKey.parentFieldNames.Length; i++) {
                                        modifyValues[foreignKey.childFieldNames[i]] = null;
                                    }
                                    await this.UpdateAsync(foreignKey.childTable.Name, modifyValues);
                                    break;
                            }
                        }
                    }
                }
                this.database.DeleteCount++;
            }

            return count;
        }

        protected abstract Task<int> DoDeleteAsync(string executableSql, Dict executableParams);

        protected async Task<object> GetKeyValue(TableIndex whereIndex, Dict varsDict, Dict executableParams, StatementEqualsRef[] whereRefs, TableIndex primaryIndex, string tableName) {
            Dict fieldValues;
            if (whereIndex.IndexType == TableIndexType.Primary) {
                fieldValues = BaseStatement.RemapStatementParamsToFieldValues(varsDict, whereRefs);
            }
            else {
                // This extra select to convert from a non-primary key index to a primary key index is unfortunate
                var selectValues = whereRefs.ToDictionary(x => x.fieldName, x => executableParams[x.fieldName]);
                fieldValues = await this.database.SelectRowAsync($"SELECT {string.Join(",", primaryIndex.FieldNames)} FROM {tableName}", selectValues);
            }
            if (fieldValues == null) {
                return null;
            }
            else {
                return fieldValues.GetKeyValue(primaryIndex.FieldNames);
            }
        }

        public async Task<bool> SynchronizeAsync(string tableName, Dict[] existingRecords, Dict[] newRecords, string[] keyFieldNames = null, Func<Dict, Task> insertFunc = null, Func<Dict, Task<int>> deleteFunc = null) {
            if (!this.database.TableByName.TryGetValue(tableName, out Table table)) throw new Exception($"Invalid table name '{tableName}'");
            if (existingRecords.Length == 0 && newRecords.Length == 0) return false;

            if (keyFieldNames == null) {
                var record = existingRecords.Length > 0 ? existingRecords[0] : newRecords[0];
                var uniqueIndex = table.FindUniqueIndex(record.Keys.ToArray());
                if (uniqueIndex == null) throw new Exception("Could not determine unique index");
                keyFieldNames = uniqueIndex.FieldNames;
            }

            bool changed = await existingRecords.SyncAsync(
                newRecords,
                keyFieldNames,
                async (newRow) => {
                    if (insertFunc == null) {
                        await this.InsertAsync<object>(table.Name, newRow);
                    }
                    else {
                        await insertFunc(newRow);
                    }
                    return true;
                },
                async (existingRow, newRow) => {
                    int count = await this.UpdateAsync(table.Name, newRow);
                    return count > 0;
                },
                async (keys, existingRow) => {
                    int count;
                    if (deleteFunc == null) {
                        count = await this.DeleteAsync(table.Name, keys);
                    }
                    else {
                        count = await deleteFunc(keys);
                    }
                    return count > 0;
                }
            );
            return changed;
        }

        // Truncate methods
        public async Task TruncateAsync(string tableName) {
            await this.DoTruncateAsync(tableName);
        }

        protected abstract Task DoTruncateAsync(string tableName);

        // Begin methods
        public abstract void Begin();

        public abstract Task BeginAsync();

        // Commit methods
        public void Commit() {
            DataEventTransaction dataEventTransaction = this.dataEvents.Count > 0 ? new DataEventTransaction(DateTime.Now, this.dataEvents.ToArray()) : null;
            if (dataEventTransaction != null) {
                this.database.PostDataEventTransaction(TransactionState.Uncommitted, dataEventTransaction);
            }
            this.DoCommit();
            if (dataEventTransaction != null) {
                this.database.PostDataEventTransaction(TransactionState.Committed, dataEventTransaction);
            }
        }

        protected abstract void DoCommit();

        public async Task CommitAsync() {
            DataEventTransaction dataEventTransaction = this.dataEvents.Count > 0 ? new DataEventTransaction(DateTime.Now, this.dataEvents.ToArray()) : null;
            if (dataEventTransaction!=null) {
                await this.database.PostDataEventTransactionAsync(TransactionState.Uncommitted, dataEventTransaction);
            }
            await this.DoCommitAsync();
            if (dataEventTransaction!=null) {
                await this.database.PostDataEventTransactionAsync(TransactionState.Committed, dataEventTransaction);
            }

            HashSet<string> onCommitKeys = new HashSet<string>();
            foreach (var onCommitRef in this.onCommitRefs) {
                if (onCommitRef.key == null || !onCommitKeys.Contains(onCommitRef.key)) {
                    await onCommitRef.onCommit();
                    if (onCommitRef.key != null) onCommitKeys.Add(onCommitRef.key);
                }
            }
        }

        public void OnCommit(Func<Task> onCommit, string key = null) {
            this.onCommitRefs.Add(new OnCommitRef(onCommit, key));
        }

        protected abstract Task DoCommitAsync();

        // Rollback methods
        public void Rollback() {
            this.DoRollback();
        }

        protected abstract void DoRollback();


        // Dispose methods
        public abstract void Dispose();

    }
}
