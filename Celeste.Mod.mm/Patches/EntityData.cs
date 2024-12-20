﻿#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it


namespace Celeste {
    class patch_EntityData : EntityData {
        /// <origdoc/>
        public extern bool orig_Has(string key);
        /// <inheritdoc cref="EntityData.Has(string)"/>
        public new bool Has(string key) {
            if (Values == null)
                return false;
            return orig_Has(key);
        }

        /// <summary>
        /// Get the <see cref="T:System.String" /> value associated with a key.
        /// An Empty or pure Whitespace value will result in the <paramref name="defaultValue"/> to be returned.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public string String(string key, string defaultValue = null) {
            if (Values != null && Values.TryGetValue(key, out object value)
                && value is string val && val.Trim().Length > 0) {
                    return val;
            }
            return defaultValue;
        }
    }
}
