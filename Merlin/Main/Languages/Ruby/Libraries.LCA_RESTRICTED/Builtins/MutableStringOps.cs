/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Math;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    using BinaryOpStorageWithScope = CallSiteStorage<Func<CallSite, RubyScope, object, object, object>>;

    [RubyClass("String", Extends = typeof(MutableString), Inherits = typeof(Object))]
    [Includes(typeof(Enumerable), typeof(Comparable))]
    public class MutableStringOps {

        [RubyConstructor]
        public static MutableString/*!*/ Create(RubyClass/*!*/ self) {
            return MutableString.CreateEmpty();
        }
        
        [RubyConstructor]
        public static MutableString/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ value) {
            return MutableString.Create(value);
        }

        [RubyConstructor]
        public static MutableString/*!*/ Create(RubyClass/*!*/ self, [NotNull]byte[]/*!*/ value) {
            return MutableString.CreateBinary(value);
        }

        #region Helpers

        internal static bool InExclusiveRangeNormalized(int length, ref int index) {
            if (index < 0) {
                index = index + length;
            }
            return index >= 0 && index < length;
        }

        private static bool InInclusiveRangeNormalized(MutableString/*!*/ str, ref int index) {
            if (index < 0) {
                index = index + str.Length;
            }
            return index >= 0 && index <= str.Length;
        }

        internal static bool NormalizeSubstringRange(ConversionStorage<int>/*!*/ fixnumCast, Range/*!*/ range, int length, out int begin, out int count) {
            begin = Protocols.CastToFixnum(fixnumCast, range.Begin);
            int end = Protocols.CastToFixnum(fixnumCast, range.End);

            begin = IListOps.NormalizeIndex(length, begin);
            if (begin < 0 || begin > length) {
                count = 0;
                return false;
            }

            end = IListOps.NormalizeIndex(length, end); 

            count = range.ExcludeEnd ? end - begin : end - begin + 1;
            return true;
        }

        internal static bool NormalizeSubstringRange(int length, ref int start, ref int count) {
            start = IListOps.NormalizeIndex(length, start);

            if (start == length || start < 0 || start > length || count < 0) {
                return false;
            }

            if (start + count > length) {
                count = length - start;
            }

            return true;
        }

        internal static int NormalizeInsertIndex(int index, int length) {
            int result = index < 0 ? index + length + 1 : index;
            if (result > length || result < 0) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", index);
            }
            return result;
        }
        
        // Parses interval strings that are of this form:
        //
        // abc         # abc
        // abc-efg-h   # abcdefgh
        // ^abc        # all characters in range 0-255 except abc
        // \x00-0xDD   # all characters in hex range
        public class IntervalParser {
            private readonly MutableString/*!*/ _range;
            private int _pos;
            private bool _rangeStarted;
            private int _startRange;

            public IntervalParser(MutableString/*!*/ range) {
                _range = range;
                _pos = 0;
                _rangeStarted = false;
            }

            public int PeekChar() {
                return _pos >= _range.Length ? -1 : _range.GetChar(_pos);
            }

            public int GetChar() {
                return _pos >= _range.Length ? -1 : _range.GetChar(_pos++);
            }

            public int NextToken() {
                int current = GetChar();

                if (current == '\\') {
                    int next = PeekChar();
                    switch (next) {
                        case 'x':
                            _pos++;
                            int digit1 = Tokenizer.ToDigit(GetChar());
                            int digit2 = Tokenizer.ToDigit(GetChar());

                            if (digit1 >= 16) {
                                throw RubyExceptions.CreateArgumentError("Invalid escape character syntax");
                            }

                            if (digit2 >= 16) {
                                current = digit1;
                            } else {
                                current = ((digit1 << 4) + digit2);
                            }
                            break;

                        case 't':
                            current = '\t';
                            break;

                        case 'n':
                            current = '\n';
                            break;

                        case 'r':
                            current = '\r';
                            break;

                        case 'v':
                            current = '\v';
                            break;

                        case '\\':
                            current = '\\';
                            break;

                        default:
                            break;
                    }
                }

                return current;
            }

            // TODO: refactor this and Parse()
            public MutableString/*!*/ ParseSequence() {
                _pos = 0;

                MutableString result = MutableString.CreateBinary();
                if (_range.Length == 0) {
                    return result;
                }

                bool negate = false;
                if (_range.GetChar(0) == '^') {
                    // Special case of ^
                    if (_range.Length == 1) {
                        result.Append('^');
                        return result;
                    }

                    negate = true;
                    _pos = 1;
                }

                BitArray array = new BitArray(256);
                array.Not();

                int c;
                while ((c = NextToken()) != -1) {
                    if (_rangeStarted) {
                        // _startRange - c. ignore ranges which are the reverse sequence
                        if (_startRange <= c) {
                            for (int i = _startRange; i <= c; ++i) {
                                if (negate) {
                                    array.Set(i, false);
                                } else {
                                    result.Append((byte)i);
                                }
                            }
                        }
                        _rangeStarted = false;
                    } else {
                        int p = PeekChar();
                        if (p == '-') {
                            // z- is treated as a literal 'z', '-'
                            if (_pos == _range.Length - 1) {
                                if (negate) {
                                    array.Set(c, false);
                                    array.Set('-', false);
                                } else {
                                    result.Append((byte)c);
                                    result.Append('-');
                                }
                                break;
                            }

                            _startRange = c;
                            if (_rangeStarted) {
                                if (negate) {
                                    array.Set('-', false);
                                } else {
                                    result.Append('-');
                                }
                                _rangeStarted = false;
                            } else {
                                _rangeStarted = true;
                            }
                            _pos++; // consume -
                        } else {
                            if (negate) {
                                array.Set(c, false);
                            } else {
                                result.Append((byte)c);
                            }
                        }
                    }
                }

                if (negate) {
                    for (int i = 0; i < 256; i++) {
                        if (array.Get(i)) {
                            result.Append((byte)i);
                        }
                    }
                }
                return result;
            }

            public BitArray/*!*/ Parse() {
                _pos = 0;

                BitArray result = new BitArray(256);
                if (_range.Length == 0) {
                    return result;
                }

                bool negate = false;
                if (_range.GetChar(0) == '^') {
                    // Special case of ^
                    if (_range.Length == 1) {
                        result.Set('^', true);
                        return result;
                    }

                    negate = true;
                    _pos = 1;
                    result.Not();
                }

                int c;
                while ((c = NextToken()) != -1) {
                    if (_rangeStarted) {
                        // _startRange - c. ignore ranges which are the reverse sequence
                        if (_startRange <= c) {
                            for (int i = _startRange; i <= c; ++i)
                                result.Set(i, !negate);
                        }
                        _rangeStarted = false;
                    } else {
                        int p = PeekChar();
                        if (p == '-') {
                            // z- is treated as a literal 'z', '-'
                            if (_pos == _range.Length - 1) {
                                result.Set(c, !negate);
                                result.Set('-', !negate);
                                break;
                            }

                            _startRange = c;
                            if (_rangeStarted) {
                                result.Set('-', !negate);
                                _rangeStarted = false;
                            } else {
                                _rangeStarted = true;
                            }
                            _pos++; // consume -
                        } else {
                            result.Set(c, !negate);
                        }
                    }
                }

                return result;
            }
        }

        public class RangeParser {

            private readonly MutableString[]/*!*/ _ranges;

            public RangeParser(params MutableString[]/*!*/ ranges) {
                ContractUtils.RequiresNotNull(ranges, "ranges");
                _ranges = ranges;
            }

            public BitArray Parse() {
                BitArray result = new IntervalParser(_ranges[0]).Parse();
                for (int i = 1; i < _ranges.Length; i++) {
                    result.And(new IntervalParser(_ranges[i]).Parse());
                }
                return result;
            }
        }

        #endregion


        #region initialize, initialize_copy

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Reinitialize(MutableString/*!*/ self) {
            return self;
        }

        // "initialize" not called when a factory/non-default ctor is called.
        // "initialize_copy" called from "dup" and "clone"
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Reinitialize(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString other) {
            return Replace(self, other);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Reinitialize(MutableString/*!*/ self, [NotNull]byte[] other) {
            self.Clear();
            self.Append(other);
            return self;
        }

        #endregion


        #region %, *, +

        [RubyMethod("%")]
        public static MutableString/*!*/ Format(StringFormatterSiteStorage/*!*/ storage, MutableString/*!*/ self, [NotNull]IList/*!*/ args) {
            StringFormatter formatter = new StringFormatter(storage, self.ConvertToString(), self.Encoding, args);
            return formatter.Format().TaintBy(self);
        }

        [RubyMethod("%")]
        public static MutableString/*!*/ Format(StringFormatterSiteStorage/*!*/ storage, ConversionStorage<IList>/*!*/ arrayTryCast,
            MutableString/*!*/ self, object args) {
            return Format(storage, self, Protocols.TryCastToArray(arrayTryCast, args) ?? new[] { args });
        }

        // encoding aware
        [RubyMethod("*")]
        public static MutableString/*!*/ Repeat(MutableString/*!*/ self, [DefaultProtocol]int times) {
            if (times < 0) {
                throw RubyExceptions.CreateArgumentError("negative argument");
            }

            return self.CreateInstance().TaintBy(self).AppendMultiple(self, times);
        }

        // encoding aware
        [RubyMethod("+")]
        public static MutableString/*!*/ Concatenate(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            // doesn't create a subclass:
            return MutableString.Create(self).Append(other).TaintBy(self).TaintBy(other);
        }

        #endregion

        #region <<, concat

        // encoding aware
        [RubyMethod("<<")]
        [RubyMethod("concat")]
        public static MutableString/*!*/ Append(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            return self.Append(other).TaintBy(other);
        }

        [RubyMethod("<<")]
        [RubyMethod("concat")]
        public static MutableString/*!*/ Append(MutableString/*!*/ self, int c) {
            if (c < 0 || c > 255) {
                throw RubyExceptions.CreateTypeConversionError("Fixnum", "String");
            }

            return self.Append((byte)c);
        }

        #endregion

        #region <=>, ==, ===

        [RubyMethod("<=>")]
        public static int Compare(MutableString/*!*/ self, [NotNull]MutableString/*!*/ other) {
            return Math.Sign(self.CompareTo(other));
        }

        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ comparisonStorage, RespondToStorage/*!*/ respondToStorage, object/*!*/ self, object other) {
            // Self is object so that we can reuse this method.

            // We test to see if other responds to to_str AND <=>
            // Ruby never attempts to convert other to a string via to_str and call Compare ... which is strange -- feels like a BUG in Ruby

            if (Protocols.RespondTo(respondToStorage, other, "to_str") && Protocols.RespondTo(respondToStorage, other, "<=>")) {
                var site = comparisonStorage.GetCallSite("<=>");
                object result = Integer.TryUnaryMinus(site.Target(site, other, self));
                if (result == null) {
                    throw RubyExceptions.CreateTypeError("{0} can't be coerced into Fixnum",
                        comparisonStorage.Context.GetClassDisplayName(result));
                }

                return result;
            }

            return null;
        }

        [RubyMethod("eql?")]
        public static bool Eql(MutableString/*!*/ lhs, [NotNull]MutableString/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("eql?")]
        public static bool Eql(MutableString/*!*/ lhs, object rhs) {
            return object.ReferenceEquals(lhs, rhs);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool StringEquals(MutableString/*!*/ lhs, [NotNull]MutableString/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equals(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ equalsStorage,
            object/*!*/ self, object other) {
            // Self is object so that we can reuse this method.

            if (!Protocols.RespondTo(respondToStorage, other, "to_str")) {
                return false;
            }

            var equals = equalsStorage.GetCallSite("==");
            return Protocols.IsTrue(equals.Target(equals, other, self));
        }

        #endregion


        #region slice!

        [RubyMethod("slice!")]
        public static object RemoveCharInPlace(RubyContext/*!*/ context, MutableString/*!*/ self, [DefaultProtocol]int index) {
            if (!InExclusiveRangeNormalized(self.GetByteCount(), ref index)) {
                return null;
            }

            // TODO: optimize if the value is not read:
            int result = self.GetByte(index);
            self.Remove(index, 1);
            return result;
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(MutableString/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int length) {
            if (length < 0) {
                return null;
            }

            if (!InInclusiveRangeNormalized(self, ref start)) {
                return null;
            }

            if (start + length > self.Length) {
                length = self.Length - start;
            }

            MutableString result = self.CreateInstance().Append(self, start, length).TaintBy(self);
            self.Remove(start, length);
            return result;
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(ConversionStorage<int>/*!*/ fixnumCast, 
            MutableString/*!*/ self, [NotNull]Range/*!*/ range) {
            int begin = Protocols.CastToFixnum(fixnumCast, range.Begin);
            int end = Protocols.CastToFixnum(fixnumCast, range.End);

            if (!InInclusiveRangeNormalized(self, ref begin)) {
                return null;
            }

            end = IListOps.NormalizeIndex(self.Length, end);

            int count = range.ExcludeEnd ? end - begin : end - begin + 1;
            return count < 0 ? self.CreateInstance() : RemoveSubstringInPlace(self, begin, count);
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            if (regex.IsEmpty) {
                return self.Clone().TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null) {
                return null;
            }

            return RemoveSubstringInPlace(self, match.Index, match.Length).TaintBy(regex, scope);
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int occurrance) {

            if (regex.IsEmpty) {
                return self.Clone().TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null || !RegexpOps.NormalizeGroupIndex(ref occurrance, match.GroupCount)) {
                return null;
            }

            return match.GroupSuccess(occurrance) ?
                RemoveSubstringInPlace(self, match.GetGroupStart(occurrance), match.GetGroupLength(occurrance)).TaintBy(regex, scope) : null;
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(MutableString/*!*/ self, [NotNull]MutableString/*!*/ searchStr) {
            if (searchStr.IsEmpty) {
                return searchStr.Clone();
            }

            int index = self.IndexOf(searchStr);
            if (index < 0) {
                return null;
            }

            RemoveSubstringInPlace(self, index, searchStr.Length);
            return searchStr.Clone();
        }

        #endregion

        #region [], slice

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static object GetChar(MutableString/*!*/ self, [DefaultProtocol]int index) {
            return InExclusiveRangeNormalized(self.GetByteCount(), ref index) ? ScriptingRuntimeHelpers.Int32ToObject(self.GetByte(index)) : null;
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(MutableString/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int count) {
            int byteCount = self.GetByteCount();
            if (!NormalizeSubstringRange(byteCount, ref start, ref count)) {
                return (start == byteCount) ? self.CreateInstance().TaintBy(self) : null;
            }

            return self.CreateInstance().Append(self, start, count).TaintBy(self);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(ConversionStorage<int>/*!*/ fixnumCast, MutableString/*!*/ self, [NotNull]Range/*!*/ range) {
            int begin, count;
            if (!NormalizeSubstringRange(fixnumCast, range, self.GetByteCount(), out begin, out count)) {
                return null;
            }
            return (count < 0) ? self.CreateInstance().TaintBy(self) : GetSubstring(self, begin, count);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(MutableString/*!*/ self, [NotNull]MutableString/*!*/ searchStr) {
            return (self.IndexOf(searchStr) != -1) ? searchStr.Clone() : null;
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            if (regex.IsEmpty) {
                return self.CreateInstance().TaintBy(self).TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null) {
                return null;
            }

            return self.CreateInstance().TaintBy(self).Append(self, match.Index, match.Length).TaintBy(regex, scope);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int occurrance) {
            if (regex.IsEmpty) {
                return self.CreateInstance().TaintBy(self).TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null || !RegexpOps.NormalizeGroupIndex(ref occurrance, match.GroupCount)) {
                return null;
            }

            MutableString result = match.AppendGroupValue(occurrance, self.CreateInstance());
            return result != null ? result.TaintBy(regex, scope) : null;
        }

        #endregion

        #region []=

        [RubyMethod("[]=")]
        public static MutableString/*!*/ ReplaceCharacter(MutableString/*!*/ self,
            [DefaultProtocol]int index, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            index = index < 0 ? index + self.Length : index;
            if (index < 0 || index >= self.Length) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", index);
            }

            if (value.IsEmpty) {
                self.Remove(index, 1).TaintBy(value);
                return MutableString.CreateEmpty();
            }

            self.Replace(index, 1, value).TaintBy(value);
            return value;
        }

        [RubyMethod("[]=")]
        public static int SetCharacter(MutableString/*!*/ self, 
            [DefaultProtocol]int index, int value) {

            index = index < 0 ? index + self.Length : index;
            if (index < 0 || index >= self.Length) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", index);
            }

            self.SetByte(index, unchecked((byte)value));
            return value;
        }

        [RubyMethod("[]=")]
        public static MutableString/*!*/ ReplaceSubstring(MutableString/*!*/ self, 
            [DefaultProtocol]int start, [DefaultProtocol]int charsToOverwrite, [DefaultProtocol, NotNull]MutableString/*!*/ value) {
            
            if (charsToOverwrite < 0) {
                throw RubyExceptions.CreateIndexError("negative length {0}", charsToOverwrite);
            }

            if (System.Math.Abs(start) > self.Length) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", start);
            }

            start = start < 0 ? start + self.Length : start;

            if (charsToOverwrite <= value.Length) {
                int insertIndex = start + charsToOverwrite;
                int limit = charsToOverwrite;
                if (insertIndex > self.Length) {
                    limit -= insertIndex - self.Length;
                    insertIndex = self.Length;
                }

                self.Replace(start, limit, value);
            } else {
                self.Replace(start, value.Length, value);

                int pos = start + value.Length;
                int charsToRemove = charsToOverwrite - value.Length;
                int charsLeftInString = self.Length - pos;

                self.Remove(pos, System.Math.Min(charsToRemove, charsLeftInString));
            }

            self.TaintBy(value);
            return value;
        }

        [RubyMethod("[]=")]
        public static MutableString/*!*/ ReplaceSubstring(ConversionStorage<int>/*!*/ fixnumCast, MutableString/*!*/ self, 
            [NotNull]Range/*!*/ range, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            int begin = Protocols.CastToFixnum(fixnumCast, range.Begin);
            int end = Protocols.CastToFixnum(fixnumCast, range.End);

            begin = begin < 0 ? begin + self.Length : begin;

            if (begin < 0 || begin > self.Length) {
                throw RubyExceptions.CreateRangeError("{0}..{1} out of range", begin, end);
            }

            end = end < 0 ? end + self.Length : end;

            int count = range.ExcludeEnd ? end - begin : end - begin + 1;
            return ReplaceSubstring(self, begin, count, value);
        }

        [RubyMethod("[]=")]
        public static MutableString ReplaceSubstring(MutableString/*!*/ self,
            [NotNull]MutableString/*!*/ substring, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            int index = self.IndexOf(substring);
            if (index == -1) {
                throw RubyExceptions.CreateIndexError("string not matched");
            }

            return ReplaceSubstring(self, index, substring.Length, value);
        }

        [RubyMethod("[]=")]
        public static MutableString ReplaceSubstring(RubyContext/*!*/ context, MutableString/*!*/ self,
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            MatchData match = regex.Match(context.KCode, self);
            if (match == null) {
                throw RubyExceptions.CreateIndexError("regexp not matched");
            }

            return ReplaceSubstring(self, match.Index, match.Length, value);
        }

        #endregion

        #region casecmp, capitalize, capitalize!, downcase, downcase!, swapcase, swapcase!, upcase, upcase!

        public static bool UpCaseChar(MutableString/*!*/ self, int index) {
            char current = self.GetChar(index);
            if (current >= 'a' && current <= 'z') {
                self.SetChar(index, current.ToUpperInvariant());
                return true;
            }
            return false;
        }

        public static bool DownCaseChar(MutableString/*!*/ self, int index) {
            char current = self.GetChar(index);
            if (current >= 'A' && current <= 'Z') {
                self.SetChar(index, current.ToLowerInvariant());
                return true;
            }
            return false;
        }

        public static bool SwapCaseChar(MutableString/*!*/ self, int index) {
            char current = self.GetChar(index);
            if (current >= 'A' && current <= 'Z') {
                self.SetChar(index, current.ToLowerInvariant());
                return true;
            } else if (current >= 'a' && current <= 'z') {
                self.SetChar(index, current.ToUpperInvariant());
                return true;
            }
            return false;
        }

        public static bool CapitalizeMutableString(MutableString/*!*/ str) {
            bool changed = false;
            if (str.Length > 0) {
                if (UpCaseChar(str, 0)) {
                    changed = true;
                }
                for (int i = 1; i < str.Length; ++i) {
                    if (DownCaseChar(str, i)) {
                        changed = true;
                    }
                }
            }
            return changed;
        }

        public static bool UpCaseMutableString(MutableString/*!*/ str) {
            bool changed = false;
            for (int i = 0; i < str.Length; ++i) {
                if (UpCaseChar(str, i)) {
                    changed = true;
                }
            }
            return changed;
        }

        public static bool DownCaseMutableString(MutableString/*!*/ str) {
            bool changed = false;
            for (int i = 0; i < str.Length; ++i) {
                if (DownCaseChar(str, i)) {
                    changed = true;
                }
            }
            return changed;
        }

        public static bool SwapCaseMutableString(MutableString/*!*/ str) {
            bool changed = false;
            for (int i = 0; i < str.Length; ++i) {
                if (SwapCaseChar(str, i)) {
                    changed = true;
                }
            }
            return changed;
        }

        [RubyMethod("casecmp")]
        public static int Casecmp(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            return Compare(DownCase(self), DownCase(other));
        }

        [RubyMethod("capitalize")]
        public static MutableString/*!*/ Capitalize(MutableString/*!*/ self) {
            MutableString result = self.Clone();
            CapitalizeMutableString(result);
            return result;
        }

        [RubyMethod("capitalize!")]
        public static MutableString CapitalizeInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();
            return CapitalizeMutableString(self) ? self : null;
        }

        [RubyMethod("downcase")]
        public static MutableString/*!*/ DownCase(MutableString/*!*/ self) {
            MutableString result = self.Clone();
            DownCaseMutableString(result);
            return result;
        }

        [RubyMethod("downcase!")]
        public static MutableString DownCaseInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();
            return DownCaseMutableString(self) ? self : null;
        }

        [RubyMethod("swapcase")]
        public static MutableString/*!*/ SwapCase(MutableString/*!*/ self) {
            MutableString result = self.Clone();
            SwapCaseMutableString(result);
            return result;
        }

        [RubyMethod("swapcase!")]
        public static MutableString SwapCaseInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();
            return SwapCaseMutableString(self) ? self : null;
        }

        [RubyMethod("upcase")]
        public static MutableString/*!*/ UpCase(MutableString/*!*/ self) {
            MutableString result = self.Clone();
            UpCaseMutableString(result);
            return result;
        }

        [RubyMethod("upcase!")]
        public static MutableString UpCaseInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();
            return UpCaseMutableString(self) ? self : null;
        }

        #endregion


        #region center

        private static readonly MutableString _DefaultPadding = MutableString.CreateAscii(" ").Freeze();

        [RubyMethod("center")]
        public static MutableString/*!*/ Center(MutableString/*!*/ self, 
            [DefaultProtocol]int length,
            [Optional, DefaultProtocol]MutableString padding) {

            if (padding != null && padding.Length == 0) {
                throw RubyExceptions.CreateArgumentError("zero width padding");
            }

            if (self.Length >= length) {
                return self;
            }

            if (padding == null) {
                padding = _DefaultPadding;
            }

            char[] charArray = new char[length];
            int n = (length - self.Length) / 2;

            for (int i = 0; i < n; i++) {
                charArray[i] = padding.GetChar(i % padding.Length);
            }

            for (int i = 0; i < self.Length; i++) {
                charArray[n + i] = self.GetChar(i);
            }

            int m = length - self.Length - n;
            for (int i = 0; i < m; i++) {
                charArray[n + self.Length + i] = padding.GetChar(i % padding.Length);
            }

            return self.CreateInstance().Append(new String(charArray)).TaintBy(self).TaintBy(padding); 
        }

        #endregion


        #region chomp, chomp!, chop, chop!

        private static bool EndsWith(MutableString/*!*/ str, MutableString/*!*/ terminator) {
            int offset = str.Length - terminator.Length;
            if (offset < 0) {
                return false;
            }

            if (str.IsBinary) {
                for (int i = 0; i < terminator.Length; i++) {
                    if (str.GetChar(offset + i) != terminator.GetChar(i)) {
                        return false;
                    }
                }
            } else {
                for (int i = 0; i < terminator.Length; i++) {
                    if (str.GetByte(offset + i) != terminator.GetByte(i)) {
                        return false;
                    }
                }
            }

            return true;
        }

        private static MutableString/*!*/ ChompTrailingCarriageReturns(MutableString/*!*/ str, bool removeCarriageReturnsToo) {
            int end = str.Length;
            while (true) {
                if (end > 1) {
                    if (str.GetChar(end - 1) == '\n') {
                        end -= str.GetChar(end - 2) == '\r' ? 2 : 1;
                    } else if (removeCarriageReturnsToo && str.GetChar(end - 1) == '\r') {
                        end -= 1;
                    }
                    else {
                        break;
                    }
                } else if (end > 0) {
                    if (str.GetChar(end - 1) == '\n' || str.GetChar(end - 1) == '\r') {
                        end -= 1;
                    }
                    break;
                } else {
                    break;
                }
            }
            return str.GetSlice(0, end);
        }

        private static MutableString InternalChomp(MutableString/*!*/ self, MutableString separator) {
            if (separator == null) {
                return self.Clone();
            }

            // Remove multiple trailing CR/LFs
            if (separator.Length == 0) {
                return ChompTrailingCarriageReturns(self, false).TaintBy(self);
            }

            // Remove single trailing CR/LFs
            MutableString result = self.Clone();
            int length = result.Length;
            if (separator.Length == 1 && separator.GetChar(0) == '\n') {
                if (length > 1 && result.GetChar(length - 2) == '\r' && result.GetChar(length - 1) == '\n') {
                    result.Remove(length - 2, 2);
                } else if (length > 0 && (self.GetChar(length - 1) == '\n' || result.GetChar(length - 1) == '\r')) {
                    result.Remove(length - 1, 1);
                }
            } else if (EndsWith(result, separator)) {
                result.Remove(length - separator.Length, separator.Length);
            }

            return result;
        }

        [RubyMethod("chomp")]
        public static MutableString/*!*/ Chomp(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return InternalChomp(self, context.InputSeparator);
        }

        [RubyMethod("chomp")]
        public static MutableString/*!*/ Chomp(MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            return InternalChomp(self, separator);
        }

        [RubyMethod("chomp!")]
        public static MutableString/*!*/ ChompInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return ChompInPlace(self, context.InputSeparator);
        }

        [RubyMethod("chomp!")]
        public static MutableString ChompInPlace(MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            MutableString result = InternalChomp(self, separator);

            if (result.Equals(self) || result == null) {
                return null;
            }

            self.Clear();
            self.Append(result);
            return self;
        }

        private static MutableString/*!*/ ChopInteral(MutableString/*!*/ self) {
            if (self.Length == 1 || self.GetChar(self.Length - 2) != '\r' || self.GetChar(self.Length - 1) != '\n') {
                self.Remove(self.Length - 1, 1);
            } else {
                self.Remove(self.Length - 2, 2);
            }
            return self;
        }

        [RubyMethod("chop!")]
        public static MutableString ChopInPlace(MutableString/*!*/ self) {
            if (self.Length == 0) return null;
            return ChopInteral(self);
        }

        [RubyMethod("chop")]
        public static MutableString/*!*/ Chop(MutableString/*!*/ self) {
            return (self.Length == 0) ? self.CreateInstance().TaintBy(self) : ChopInteral(self.Clone());
        }

        #endregion


        #region dump, inspect

        public static string/*!*/ GetQuotedStringRepresentation(MutableString/*!*/ self, RubyContext/*!*/ context, bool isDump, char quote) {
            bool is18 = context.RubyOptions.Compatibility == RubyCompatibility.Ruby18;
            
            return self.AppendRepresentation(
                new StringBuilder().Append(quote),
                (isDump || !is18) ? null : context.KCode ?? RubyEncoding.Binary,
                MutableString.Escape.Special | (is18 ? MutableString.Escape.Octal : 0) | (isDump ? MutableString.Escape.NonAscii : 0),
                quote
            ).Append(quote).ToString();
        }

        // encoding aware
        [RubyMethod("dump")]
        public static MutableString/*!*/ Dump(RubyContext/*!*/ context, MutableString/*!*/ self) {
            // Note that "self" could be a subclass of MutableString, and the return value should be
            // of the same type
            return self.CreateInstance().Append(GetQuotedStringRepresentation(self, context, true, '"')).TaintBy(self);
        }

        // encoding aware, uses the current KCODE
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, MutableString/*!*/ self) {
            // Note that "self" could be a subclass of MutableString, but the return value should 
            // always be just a MutableString
            return MutableString.Create(GetQuotedStringRepresentation(self, context, false, '"'), context.KCode ?? self.Encoding).TaintBy(self);
        }

        #endregion

        #region each, each_byte, each_line

        // encoding aware
        [RubyMethod("each_byte")]
        public static object EachByte(BlockParam block, MutableString/*!*/ self) {
            if (block == null && !self.IsEmpty) {
                throw RubyExceptions.NoBlockGiven();
            }

            // MRI allows the string to be changed during iteration
            int i = 0;
            while (i < self.GetByteCount()) {
                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject((int)self.GetByte(i)), out result)) {
                    return result;
                }
                i++;
            }
            return self;
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(RubyContext/*!*/ context, BlockParam block, MutableString/*!*/ self) {
            return EachLine(block, self, context.InputSeparator);
        }

        internal static readonly MutableString DefaultLineSeparator = MutableString.CreateAscii("\n").Freeze();
        internal static readonly MutableString DefaultParagraphSeparator = MutableString.CreateAscii("\n\n").Freeze();

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(BlockParam block, MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            return EachLine(block, self, separator, 0);
        }

        public static object EachLine(BlockParam block, MutableString/*!*/ self, [DefaultProtocol]MutableString separator, int start) {
            self.TrackChanges();

            MutableString paragraphSeparator;
            if (separator == null || separator.IsEmpty) {
                separator = DefaultLineSeparator;
                paragraphSeparator = DefaultParagraphSeparator;
            } else {
                paragraphSeparator = null;
            }

            // TODO: this is slow, refactor when we redo MutableString
            MutableString str = self;

            // In "normal" mode just split the string at the end of each seperator occurrance.
            // In "paragraph" mode, split the string at the end of each occurrance of two or more
            // successive seperators.
            while (start < self.Length) {
                int end;
                if (paragraphSeparator == null) {
                    end = str.IndexOf(separator, start);
                    if (end >= 0) {
                        end += separator.Length;
                    } else {
                        end = str.Length;
                    }
                } else {
                    end = str.IndexOf(paragraphSeparator, start);
                    if (end >= 0) {
                        end += (2 * separator.Length);
                        while (str.IndexOf(separator, end) == end) {
                            end += separator.Length;
                        }
                    } else {
                        end = str.Length;
                    }
                }

                // Yield the current line
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                object result;
                MutableString line = self.CreateInstance().TaintBy(self).Append(str, start, end - start);
                if (block.Yield(line, out result)) {
                    return result;
                }

                start = end;
            }

            // MRI 1.8: this is checked after each line
            // MRI 1.9: not checked at all
            // Ensure that the underlying string has not been mutated during the iteration
            RequireNoVersionChange(self);
            return self;
        }

        #endregion

        #region empty?, size, length, encoding

        // encoding aware
        [RubyMethod("empty?")]
        public static bool IsEmpty(MutableString/*!*/ self) {
            return self.IsEmpty;
        }

        // encoding aware
        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int GetLength(MutableString/*!*/ self) {
            return (self.Encoding.IsKCoding) ? self.GetByteCount() : self.GetCharCount();
        }

        // encoding aware
        [RubyMethod("encoding", Compatibility=RubyCompatibility.Ruby19)]
        public static RubyEncoding/*!*/ GetEncoding(MutableString/*!*/ self) {
            return self.Encoding;
        }

        #endregion


        #region sub, gsub

        // returns true if block jumped
        // "result" will be null if there is no successful match
        private static bool BlockReplaceFirst(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, MutableString/*!*/ input, BlockParam/*!*/ block,
            RubyRegex/*!*/ pattern, out object blockResult, out MutableString result) {

            var matchScope = scope.GetInnerMostClosureScope();
            MatchData match = RegexpOps.Match(scope, pattern, input);
            if (match == null) {
                result = null;
                blockResult = null;
                matchScope.CurrentMatch = null;
                return false;
            }

            // copy upfront so that no modifications to the input string are included in the result:
            result = input.Clone();
            matchScope.CurrentMatch = match;

            if (block.Yield(match.GetValue(), out blockResult)) {
                return true;
            }

            // resets the $~ scope variable to the last match (skipped if block jumped):
            matchScope.CurrentMatch = match;

            MutableString replacement = Protocols.ConvertToString(tosConversion, blockResult);
            result.TaintBy(replacement);

            // Note - we don't interpolate special sequences like \1 in block return value
            result.Replace(match.Index, match.Length, replacement);

            blockResult = null;
            return false;
        }
        
        // returns true if block jumped
        // "result" will be null if there is no successful match
        private static bool BlockReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, MutableString/*!*/ input, BlockParam/*!*/ block,
            RubyRegex/*!*/ regex, out object blockResult, out MutableString result) {

            var matchScope = scope.GetInnerMostClosureScope();

            var matches = regex.Matches(tosConversion.Context.KCode, input);
            if (matches.Count == 0) {
                result = null;
                blockResult = null;
                matchScope.CurrentMatch = null;
                return false;
            }

            // create an empty result:
            result = input.CreateInstance().TaintBy(input);
            
            int offset = 0;
            foreach (MatchData match in matches) {
                matchScope.CurrentMatch = match;

                input.TrackChanges();
                if (block.Yield(match.GetValue(), out blockResult)) {
                    return true;
                }
                if (input.HasChanged) {
                    return false;
                }

                // resets the $~ scope variable to the last match (skipd if block jumped):
                matchScope.CurrentMatch = match;

                MutableString replacement = Protocols.ConvertToString(tosConversion, blockResult);
                result.TaintBy(replacement);

                // prematch:
                result.Append(input, offset, match.Index - offset);

                // replacement (unlike ReplaceAll, don't interpolate special sequences like \1 in block return value):
                result.Append(replacement);

                offset = match.Index + match.Length;
            }

            // post-last-match:
            result.Append(input, offset, input.Length - offset);

            blockResult = null;
            return false;
        }

        private static void AppendBackslashes(int backslashCount, MutableString/*!*/ result, int minBackslashes) {
            for (int j = 0; j < ((backslashCount - 1) >> 1) + minBackslashes; j++) {
                result.Append('\\');
            }
        }

        private static void AppendReplacementExpression(MutableString/*!*/ input, MatchData/*!*/ match, MutableString/*!*/ result, 
            MutableString/*!*/ replacement) {

            int backslashCount = 0;
            for (int i = 0; i < replacement.Length; i++) {
                char c = replacement.GetChar(i);
                if (c == '\\') {
                    backslashCount++;
                } else if (backslashCount == 0) {
                    result.Append(c);
                } else {
                    AppendBackslashes(backslashCount, result, 0);
                    // Odd number of \'s + digit means insert replacement expression
                    if ((backslashCount & 1) == 1) {
                        if (Char.IsDigit(c)) {
                            AppendGroupByIndex(match, c - '0', result);
                        } else if (c == '&') {
                            AppendGroupByIndex(match, match.GroupCount - 1, result);
                        } else if (c == '`') {
                            // Replace with everything in the input string BEFORE the match
                            result.Append(input, 0, match.Index);
                        } else if (c == '\'') {
                            // Replace with everything in the input string AFTER the match
                            int start = match.Index + match.Length;
                            // TODO:
                            result.Append(input, start, input.GetLength() - start);
                        } else if (c == '+') {
                            // Replace last character in last successful match group
                            AppendLastCharOfLastMatchGroup(match, result);
                        } else {
                            // unknown escaped replacement char, go ahead and replace untouched
                            result.Append('\\');
                            result.Append(c);
                        }
                    } else {
                        // Any other # of \'s or a non-digit character means insert literal \'s and character
                        AppendBackslashes(backslashCount, result, 1);
                        result.Append(c);
                    }
                    backslashCount = 0;
                }
            }
            AppendBackslashes(backslashCount, result, 1);
        }

        private static void AppendLastCharOfLastMatchGroup(MatchData/*!*/ match, MutableString/*!*/ result) {
            int i = match.GroupCount - 1;
            // move to last successful match group
            while (i > 0 && !match.GroupSuccess(i)) {
                i--;
            }

            if (i > 0 && match.GroupSuccess(i)) {
                int length = match.GetGroupLength(i);
                if (length > 0) {
                   result.Append(match.OriginalString, match.GetGroupStart(i) + length - 1, 1);
                }
            }
        }

        private static void AppendGroupByIndex(MatchData/*!*/ match, int index, MutableString/*!*/ result) {
            var value = match.GetGroupValue(index);
            if (value != null) {
                result.Append(value);
            }
        }

        private static MutableString ReplaceFirst(RubyScope/*!*/ scope, MutableString/*!*/ input, MutableString/*!*/ replacement, RubyRegex/*!*/ pattern) {
            MatchData match = RegexpOps.Match(scope, pattern, input);
            if (match == null) {
                return null;
            }

            MutableString result = input.CreateInstance().TaintBy(input).TaintBy(replacement);
            
            // prematch:
            result.Append(input, 0, match.Index);

            AppendReplacementExpression(input, match, result, replacement);

            // postmatch:
            int offset = match.Index + match.Length;
            result.Append(input, offset, input.Length - offset);

            return result;
        }

        private static MutableString ReplaceAll(RubyScope/*!*/ scope, MutableString/*!*/ input, MutableString/*!*/ replacement, 
            RubyRegex/*!*/ regex) {
            var matchScope = scope.GetInnerMostClosureScope();
            
            // case of all
            IList<MatchData> matches = regex.Matches(scope.RubyContext.KCode, input);
            if (matches.Count == 0) {
                matchScope.CurrentMatch = null;
                return null;
            }

            MutableString result = input.CreateInstance().TaintBy(input).TaintBy(replacement);

            int offset = 0;
            foreach (MatchData match in matches) {
                result.Append(input, offset, match.Index - offset);
                AppendReplacementExpression(input, match, result, replacement);
                offset = match.Index + match.Length;
            }

            result.Append(input, offset, input.Length - offset);

            matchScope.CurrentMatch = matches[matches.Count - 1];
            return result;
        }

        [RubyMethod("sub")]
        public static object BlockReplaceFirst(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ pattern) {

            object blockResult;
            MutableString result;
            return BlockReplaceFirst(tosConversion, scope, self, block, pattern, out blockResult, out result) ? blockResult : (result ?? self.Clone());
        }
        
        [RubyMethod("gsub")]
        public static object BlockReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]RubyRegex pattern) {

            object blockResult;
            MutableString result;
            self.TrackChanges();
            object r = BlockReplaceAll(tosConversion, scope, self, block, pattern, out blockResult, out result) ? blockResult : (result ?? self.Clone());

            RequireNoVersionChange(self);
            return r;
        }

        [RubyMethod("sub")]
        public static object BlockReplaceFirst(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]MutableString matchString) {

            object blockResult;
            MutableString result;
            // TODO:
            var regex = new RubyRegex(MutableString.CreateMutable(Regex.Escape(matchString.ToString()), matchString.Encoding), RubyRegexOptions.NONE);

            return BlockReplaceFirst(tosConversion, scope, self, block, regex, out blockResult, out result) ? blockResult : (result ?? self.Clone());
        }

        [RubyMethod("gsub")]
        public static object BlockReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]MutableString matchString) {

            object blockResult;
            MutableString result;
            // TODO:
            var regex = new RubyRegex(MutableString.CreateMutable(Regex.Escape(matchString.ToString()), matchString.Encoding), RubyRegexOptions.NONE);

            self.TrackChanges();
            object r = BlockReplaceAll(tosConversion, scope, self, block, regex, out blockResult, out result) ? blockResult : (result ?? self.Clone());
            RequireNoVersionChange(self);
            return r;
        }

        [RubyMethod("sub")]
        public static MutableString ReplaceFirst(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [DefaultProtocol, NotNull]MutableString/*!*/ replacement) {

            return ReplaceFirst(scope, self, replacement, pattern) ?? self.Clone();
        }

        [RubyMethod("gsub")]
        public static MutableString ReplaceAll(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [DefaultProtocol, NotNull]MutableString/*!*/ replacement) {

            return ReplaceAll(scope, self, replacement, pattern) ?? self.Clone();
        }

        #endregion


        #region sub!, gsub!

        private static object BlockReplaceInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, BlockParam/*!*/ block, MutableString/*!*/ self, 
            RubyRegex/*!*/ pattern, bool replaceAll) {

            object blockResult;

            self.TrackChanges();

            // prepare replacement in a builder:
            MutableString builder;
            if (replaceAll ?
                BlockReplaceAll(tosConversion, scope, self, block, pattern, out blockResult, out builder) :
                BlockReplaceFirst(tosConversion, scope, self, block, pattern, out blockResult, out builder)) {

                // block jumped:
                return blockResult;
            }

            // unsuccessful match:
            if (builder == null) {
                return null;
            }

            RequireNoVersionChange(self);

            if (self.IsFrozen) {
                throw RubyExceptions.CreateRuntimeError("string frozen");
            }

            // replace content of self with content of the builder:
            self.Replace(0, self.Length, builder);
            return self.TaintBy(builder);
        }

        private static MutableString ReplaceInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, RubyRegex/*!*/ pattern,
            MutableString/*!*/ replacement, bool replaceAll) {
            
            MutableString builder = replaceAll ?
                ReplaceAll(scope, self, replacement, pattern) :
                ReplaceFirst(scope, self, replacement, pattern);
            
            // unsuccessful match:
            if (builder == null) {
                return null;
            }

            self.Replace(0, self.Length, builder);
            return self.TaintBy(builder);
        }

        [RubyMethod("sub!")]
        public static object BlockReplaceFirstInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern) {

            return BlockReplaceInPlace(tosConversion, scope, block, self, pattern, false);
        }

        [RubyMethod("gsub!")]
        public static object BlockReplaceAllInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern) {

            return BlockReplaceInPlace(tosConversion, scope, block, self, pattern, true);
        }

        [RubyMethod("sub!")]
        public static MutableString ReplaceFirstInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [DefaultProtocol, NotNull]MutableString/*!*/ replacement) {

            return ReplaceInPlace(scope, self, pattern, replacement, false);
        }

        [RubyMethod("gsub!")]
        public static MutableString ReplaceAllInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [DefaultProtocol, NotNull]MutableString/*!*/ replacement) {

            return ReplaceInPlace(scope, self, pattern, replacement, true);
        }

        #endregion


        #region index, rindex

        // encoding aware
        [RubyMethod("index")]
        public static object Index(MutableString/*!*/ self, 
            [DefaultProtocol, NotNull]MutableString/*!*/ substring, [DefaultProtocol, Optional]int start) {

            self.SwitchToCharacters();
            if (!NormalizeStart(self.GetCharCount(), ref start)) {
                return null;
            }

            self.RequireCompatibleEncoding(substring);
            substring.SwitchToCharacters();

            int result = self.IndexOf(substring, start);
            return (result != -1) ? ScriptingRuntimeHelpers.Int32ToObject(result) : null;
        }

        // encoding aware
        [RubyMethod("index")]
        public static object Index(MutableString/*!*/ self, 
            int character, [DefaultProtocol, Optional]int start) {

            if (!self.IsBinaryEncoded && !self.Encoding.IsKCoding && !self.IsAscii()) {
                throw RubyExceptions.CreateTypeError("type mismatch: Fixnum given");
            }

            if (!NormalizeStart(self.GetByteCount(), ref start) || character < 0 || character > 255) {
                return null;
            }

            int result = self.IndexOf((byte)character, start);
            return (result != -1) ? ScriptingRuntimeHelpers.Int32ToObject(result) : null;
        }

        // encoding aware
        [RubyMethod("index")]
        public static object Index(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol, Optional]int start) {

            MatchData match = regex.Match(scope.RubyContext.KCode, self, start);
            scope.GetInnerMostClosureScope().CurrentMatch = match;
            return (match != null) ? ScriptingRuntimeHelpers.Int32ToObject(match.Index) : null;
        }

        // encoding aware
        [RubyMethod("rindex")]
        public static object LastIndexOf(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ substring, [DefaultProtocol, DefaultParameterValue(Int32.MaxValue)]int start) {

            self.SwitchToCharacters();
            int charCount = self.GetCharCount();

            start = IListOps.NormalizeIndex(charCount, start);
            if (start < 0) {
                return null;
            }

            if (substring.IsEmpty) {
                return ScriptingRuntimeHelpers.Int32ToObject((start >= charCount) ? charCount : start);
            }

            self.RequireCompatibleEncoding(substring);
            substring.SwitchToCharacters();
            int subCharCount = substring.GetCharCount();

            // LastIndexOf has CLR semantics: no characters of the substring are matched beyond start position.
            // Hence we need to increase start by the length of the substring - 1.
            if (start > charCount - subCharCount) {
                start = charCount - 1;
            } else {
                start += subCharCount - 1;
            }

            int result = self.LastIndexOf(substring, start);
            return (result != -1) ? ScriptingRuntimeHelpers.Int32ToObject(result) : null;
        }

        // encoding aware
        [RubyMethod("rindex")]
        public static object LastIndexOf(MutableString/*!*/ self,
            int character, [DefaultProtocol, DefaultParameterValue(Int32.MaxValue)]int start) {

            if (!self.IsBinaryEncoded && !self.Encoding.IsKCoding && !self.IsAscii()) {
                throw RubyExceptions.CreateTypeError("type mismatch: Fixnum given");
            }

            int byteCount = self.GetByteCount();
            start = IListOps.NormalizeIndex(byteCount, start);
            if (start < 0 || character < 0 || character > 255) {
                return null;
            }

            if (start >= byteCount) {
                start = byteCount - 1;
            }
            
            int result = self.LastIndexOf((byte)character, start);
            return (result != -1) ? ScriptingRuntimeHelpers.Int32ToObject(result) : null;
        }

        // encoding aware
        [RubyMethod("rindex")]
        public static object LastIndexOf(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol, DefaultParameterValue(Int32.MaxValue)]int start) {

            MatchData match = regex.LastMatch(scope.RubyContext.KCode, self, start);
            scope.GetInnerMostClosureScope().CurrentMatch = match;
            return (match != null) ? ScriptingRuntimeHelpers.Int32ToObject(match.Index) : null;
        }

        // Start in range ==> search range from the first character towards the end.
        //
        // [-length, 0)     ==> [0, length + start]
        // start < -length  ==> false
        // [0, length)      ==> [start, length)
        // start > length   ==> false
        //
        private static bool NormalizeStart(int length, ref int start) {
            start = IListOps.NormalizeIndex(length, start);
            if (start < 0 || start > length) {
                return false;
            }
            return true;
        }

        #endregion


        #region delete, delete!

        private static MutableString/*!*/ InternalDelete(MutableString/*!*/ self, MutableString[]/*!*/ ranges) {
            BitArray map = new RangeParser(ranges).Parse();
            MutableString result = self.CreateInstance().TaintBy(self);
            for (int i = 0; i < self.Length; i++) {
                if (!map.Get(self.GetChar(i))) {
                    result.Append(self.GetChar(i));
                }
            }
            return result;
        }

        private static MutableString/*!*/ InternalDeleteInPlace(MutableString/*!*/ self, MutableString[]/*!*/ ranges) {
            MutableString result = InternalDelete(self, ranges);
            if (self.Equals(result)) {
                return null;
            }

            self.Clear();
            self.Append(result);
            return self;
        }

        [RubyMethod("delete")]
        public static MutableString/*!*/ Delete(RubyContext/*!*/ context, MutableString/*!*/ self, 
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ strs) {
            if (strs.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments");
            }
            return InternalDelete(self, strs);
        }

        [RubyMethod("delete!")]
        public static MutableString/*!*/ DeleteInPlace(RubyContext/*!*/ context, MutableString/*!*/ self,
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ strs) {
            self.RequireNotFrozen();

            if (strs.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments");
            }
            return InternalDeleteInPlace(self, strs);
        }

        #endregion

        #region count
        
        private static object InternalCount(MutableString/*!*/ self, MutableString[]/*!*/ ranges) {
            BitArray map = new RangeParser(ranges).Parse();
            int count = 0;
            for (int i = 0; i < self.Length; i++) {
                if (map.Get(self.GetChar(i))) {
                    count++;
                }
            }
            return ScriptingRuntimeHelpers.Int32ToObject(count);
        }

        [RubyMethod("count")]
        public static object Count(RubyContext/*!*/ context, MutableString/*!*/ self, 
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ strs) {
            if (strs.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments");
            }
            return InternalCount(self, strs);
        }

        #endregion

        #region include?

        [RubyMethod("include?")]
        public static bool Include(MutableString/*!*/ str, [DefaultProtocol, NotNull]MutableString/*!*/ subString) {
            return str.IndexOf(subString) != -1;
        }

        [RubyMethod("include?")]
        public static bool Include(MutableString/*!*/ str, int c) {
            return str.IndexOf((byte)(c % 256)) != -1;
        }

        #endregion


        #region insert

        [RubyMethod("insert")]
        public static MutableString Insert(MutableString/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol, NotNull]MutableString/*!*/ value) {
            return self.Insert(NormalizeInsertIndex(start, self.GetLength()), value).TaintBy(value);
        }

        #endregion


        #region =~, match

        [RubyMethod("=~")]
        public static object Match(RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            return RegexpOps.MatchIndex(scope, regex, self);
        }

        [RubyMethod("=~")]
        public static object Match(MutableString/*!*/ self, [NotNull]MutableString/*!*/ str) {
            throw RubyExceptions.CreateTypeError("type mismatch: String given");
        }

        [RubyMethod("=~")]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, MutableString/*!*/ self, object obj) {
            var site = storage.GetCallSite("=~", new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf));
            return site.Target(site, scope, obj, self);
        }

        [RubyMethod("match")]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            var site = storage.GetCallSite("match", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasScope));
            return site.Target(site, scope, regex, self);
        }

        [RubyMethod("match")]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ pattern) {
            var site = storage.GetCallSite("match", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasScope));
            return site.Target(site, scope, new RubyRegex(pattern, RubyRegexOptions.NONE), self);
        }
       
        #endregion


        #region scan

        [RubyMethod("scan")]
        public static RubyArray/*!*/ Scan(RubyScope/*!*/ scope, MutableString/*!*/ self, [DefaultProtocol, NotNull]RubyRegex/*!*/ regex) {
            IList<MatchData> matches = regex.Matches(scope.RubyContext.KCode, self, false);

            var matchScope = scope.GetInnerMostClosureScope();
            
            RubyArray result = new RubyArray(matches.Count);
            if (matches.Count == 0) {
                matchScope.CurrentMatch = null;
                return result;
            } 

            foreach (MatchData match in matches) {
                result.Add(MatchToScanResult(scope, self, regex, match));
            }

            matchScope.CurrentMatch = matches[matches.Count - 1];
            return result;
        }

        [RubyMethod("scan")]
        public static object/*!*/ Scan(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, [DefaultProtocol, NotNull]RubyRegex regex) {
            var matchScope = scope.GetInnerMostClosureScope();

            IList<MatchData> matches = regex.Matches(scope.RubyContext.KCode, self);
            if (matches.Count == 0) {
                matchScope.CurrentMatch = null;
                return self;
            } 

            foreach (MatchData match in matches) {
                matchScope.CurrentMatch = match;

                object blockResult;
                if (block.Yield(MatchToScanResult(scope, self, regex, match), out blockResult)) {
                    return blockResult;
                }

                // resets the $~ scope variable to the last match (skipped if block jumped):
                matchScope.CurrentMatch = match;
            }
            return self;
        }

        private static object MatchToScanResult(RubyScope/*!*/ scope, MutableString/*!*/ self, RubyRegex/*!*/ regex, MatchData/*!*/ match) {
            if (match.GroupCount == 1) {
                return match.GetValue().TaintBy(regex, scope);
            } else {
                var result = new RubyArray(match.GroupCount - 1);
                for (int i = 1; i < match.GroupCount; i++) {
                    MutableString value = match.GetGroupValue(i);
                    result.Add(value != null ? value.TaintBy(regex, scope) : value);
                }
                return result;
            }
        }

        #endregion


        #region succ, succ!

        public static int GetIndexOfRightmostAlphaNumericCharacter(MutableString/*!*/ str, int index) {
            for (int i = index; i >= 0; --i)
                if (Char.IsLetterOrDigit(str.GetChar(i)))
                    return i;

            return -1;
        }

        // TODO: remove recursion
        public static void IncrementAlphaNumericChar(MutableString/*!*/ str, int index) {
            char c = str.GetChar(index);
            if (c == 'z' || c == 'Z' || c == '9') {
                int nextIndex = GetIndexOfRightmostAlphaNumericCharacter(str, index - 1);
                if (c == 'z') {
                    str.SetChar(index, 'a');
                    if (nextIndex == -1) {
                        str.Insert(index, 'a');
                    } else {
                        IncrementAlphaNumericChar(str, nextIndex);
                    }
                } else if (c == 'Z') {
                    str.SetChar(index, 'A');
                    if (nextIndex == -1) {
                        str.Insert(index, 'A');
                    } else {
                        IncrementAlphaNumericChar(str, nextIndex);
                    }
                } else {
                    str.SetChar(index, '0');
                    if (nextIndex == -1) {
                        str.Insert(index, '1');
                    } else {
                        IncrementAlphaNumericChar(str, nextIndex);
                    }
                }
            } else {
                IncrementChar(str, index);
            }
        }

        public static void IncrementChar(MutableString/*!*/ str, int index) {
            byte c = str.GetByte(index);
            if (c == 255) {
                str.SetByte(index, 0);
                if (index > 0) {
                    IncrementChar(str, index - 1);
                } else {
                    str.Insert(0, 1);
                }
            } else {
                str.SetByte(index, unchecked((byte)(c + 1)));
            }
        }

        [RubyMethod("succ!")]
        [RubyMethod("next!")]
        public static MutableString/*!*/ SuccInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();

            if (self.IsEmpty) {
                return self;
            }

            int index = GetIndexOfRightmostAlphaNumericCharacter(self, self.Length - 1);
            if (index == -1) {
                IncrementChar(self, self.Length - 1);
            } else {
                IncrementAlphaNumericChar(self, index);
            }

            return self;
        }

        [RubyMethod("succ")]
        [RubyMethod("next")]
        public static MutableString/*!*/ Succ(MutableString/*!*/ self) {
            return SuccInPlace(self.Clone());
        }

        #endregion


        #region split

        private static RubyArray/*!*/ MakeRubyArray(MutableString/*!*/ self, MutableString[]/*!*/ elements) {
            return MakeRubyArray(self, elements, 0, elements.Length);
        }

        private static RubyArray/*!*/ MakeRubyArray(MutableString/*!*/ self, MutableString[]/*!*/ elements, int start, int count) {
            RubyArray result = new RubyArray(elements.Length);
            for (int i = 0; i < count; i++) {
                result.Add(self.CreateInstance().Append(elements[start + i]).TaintBy(self));
            }
            return result;
        }

        // The IndexOf and InternalSplit helper methods are necessary because Ruby semantics of these methods
        // differ from .NET semantics. IndexOf("") returns the next character, which is reflected in the 
        // InternalSplit method which also flows taint

        private static int IndexOf(MutableString/*!*/ str, MutableString/*!*/ separator, int index) {
            if (separator.Length > 0) {
                return str.IndexOf(separator, index);
            } else {
                return index + 1;
            }
        }

        private static RubyArray/*!*/ WhitespaceSplit(MutableString/*!*/ self, int maxComponents) {
            char[] separators = new char[] { ' ', '\n', '\r', '\t', '\v' };
            MutableString[] elements = self.Split(separators, (maxComponents < 0) ? Int32.MaxValue : maxComponents, StringSplitOptions.RemoveEmptyEntries);

            RubyArray result = new RubyArray(); 
            foreach (MutableString element in elements) {
                result.Add(self.CreateInstance().Append(element).TaintBy(self));
            }

            // Strange behavior to match Ruby semantics
            if (maxComponents < 0) {
                result.Add(self.CreateInstance().TaintBy(self));
            }

            return result;
        }

        private static RubyArray/*!*/ InternalSplit(MutableString/*!*/ self, MutableString separator, StringSplitOptions options, int maxComponents) {
            if (separator == null || separator.Length == 1 && separator.GetChar(0) == ' ') {
                return WhitespaceSplit(self, maxComponents);
            }

            if (maxComponents <= 0) {
                maxComponents = Int32.MaxValue;
            }

            RubyArray result = new RubyArray(maxComponents == Int32.MaxValue ? 1 : maxComponents + 1);
            bool keepEmpty = (options & StringSplitOptions.RemoveEmptyEntries) != StringSplitOptions.RemoveEmptyEntries;

            int selfLength = self.Length;
            int i = 0;
            int next;
            while (maxComponents > 1 && i < selfLength && (next = IndexOf(self, separator, i)) != -1) {
                if (next > i || keepEmpty) {
                    result.Add(self.CreateInstance().Append(self, i, next - i).TaintBy(self));
                    maxComponents--;
                }

                i = next + separator.Length;
            }

            if (i < selfLength || keepEmpty) {
                result.Add(self.CreateInstance().Append(self, i, selfLength - i).TaintBy(self));
            }

            return result;
        }

        [RubyMethod("split")]
        public static RubyArray/*!*/ Split(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope, MutableString/*!*/ self) {
            return Split(stringCast, scope, self, (MutableString)null, 0);
        }

        [RubyMethod("split")]
        public static RubyArray/*!*/ Split(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [DefaultProtocol]MutableString separator, [DefaultProtocol, Optional]int limit) {

            if (separator == null) {
                object defaultSeparator = scope.RubyContext.StringSeparator;
                RubyRegex regexSeparator = defaultSeparator as RubyRegex;
                if (regexSeparator != null) {
                    return Split(stringCast, scope, self, regexSeparator, limit);
                }
                
                if (defaultSeparator != null) {
                    separator = Protocols.CastToString(stringCast, defaultSeparator);
                }
            }

            if (self.IsEmpty) {
                // If self is "", the result is always []. This is special cased because the code will
                // return [""].
                return new RubyArray();
            }

            if (limit == 0) {
                // suppress trailing empty fields
                RubyArray array = InternalSplit(self, separator, StringSplitOptions.None, Int32.MaxValue);
                while (array.Count != 0 && ((MutableString)array[array.Count - 1]).Length == 0) {
                    array.RemoveAt(array.Count - 1);
                }
                return array;
            } else if (limit == 1) {
                // returns an array with original string
                RubyArray result = new RubyArray(1);
                result.Add(self);
                return result;
            } else {
                return InternalSplit(self, separator, StringSplitOptions.None, limit);
            }
        }

        [RubyMethod("split")]
        public static RubyArray/*!*/ Split(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regexp, [DefaultProtocol, Optional]int limit) {
            
            if (regexp.IsEmpty) {
                return Split(stringCast, scope, self, MutableString.FrozenEmpty, limit);
            }

            if (self.IsEmpty) {
                // If self is "", the result is always []. This is special cased because the code will
                // return [""].
                return new RubyArray();
            }

            if (limit == 0) {
                // suppress trailing empty fields
                RubyArray array = MakeRubyArray(self, regexp.Split(stringCast.Context.KCode, self));
                while (array.Count != 0 && ((MutableString)array[array.Count - 1]).Length == 0) {
                    array.RemoveAt(array.Count - 1);
                }
                return array;
            } else if (limit == 1) {
                // returns an array with original string
                RubyArray result = new RubyArray(1);
                result.Add(self);
                return result;
            } else if (limit < 0) {
                // does not suppress trailing fields when negative 
                return MakeRubyArray(self, regexp.Split(stringCast.Context.KCode, self));
            } else {
                // limit > 1 limits to N fields
                return MakeRubyArray(self, regexp.Split(stringCast.Context.KCode, self, limit));
            }
        }

        #endregion


        #region strip, strip!, lstrip, lstrip!, rstrip, rstrip!

        [RubyMethod("strip")]
        public static MutableString/*!*/ Strip(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return Strip(self, true, true);
        }

        [RubyMethod("lstrip")]
        public static MutableString/*!*/ StripLeft(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return Strip(self, true, false);
        }

        [RubyMethod("rstrip")]
        public static MutableString/*!*/ StripRight(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return Strip(self, false, true);
        }

        [RubyMethod("strip!")]
        public static MutableString StripInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return StripInPlace(self, true, true);
        }

        [RubyMethod("lstrip!")]
        public static MutableString StripLeftInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return StripInPlace(self, true, false);
        }

        [RubyMethod("rstrip!")]
        public static MutableString StripRightInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return StripInPlace(self, false, true);
        }
        
        private static MutableString/*!*/ Strip(MutableString/*!*/ str, bool trimLeft, bool trimRight) {
            int left, right;
            GetTrimRange(str, trimLeft, trimRight, out left, out right);
            return str.GetSlice(left, right - left).TaintBy(str);
        }

        public static MutableString StripInPlace(MutableString/*!*/ self, bool trimLeft, bool trimRight) {
            int left, right;
            GetTrimRange(self, trimLeft, trimRight, out left, out right);
            int remaining = right - left;

            // nothing to trim:
            if (remaining == self.Length) {
                return null;
            }

            if (remaining == 0) {
                // all whitespace
                self.Clear();
            } else {
                self.Trim(left, remaining);
            }
            return self;
        }

        private static void GetTrimRange(MutableString/*!*/ str, bool left, bool right, out int leftIndex, out int rightIndex) {
            GetTrimRange(
                str.Length,
                !left ? (Func<int, bool>)null : (i) => Char.IsWhiteSpace(str.GetChar(i)),
                !right ? (Func<int, bool>)null : (i) => {
                    char c = str.GetChar(i);
                    return Char.IsWhiteSpace(c) || c == '\0';
                },
                out leftIndex, 
                out rightIndex
            );
        }

        // Returns indices of the first non-whitespace character (from left and from right).
        // ensures (leftIndex == rightIndex) ==> all characters are whitespace
        // leftIndex == 0, rightIndex == length if there is no whitespace to be trimmed.
        internal static void GetTrimRange(int length, Func<int, bool> trimLeft, Func<int, bool> trimRight, out int leftIndex, out int rightIndex) {
            int i;
            if (trimLeft != null) {
                i = 0;
                while (i < length) {
                    if (!trimLeft(i)) {
                        break;
                    }
                    i++;
                }
            } else {
                i = 0;
            }

            int j;
            if (trimRight != null) {
                j = length - 1;
                // we need to compare i-th character again as it could be treated as right whitespace but not as left whitespace:
                while (j >= i) {
                    if (!trimRight(j)) {
                        break;
                    }
                    j--;
                }

                // should point right after the non-whitespace character:
                j++;
            } else {
                j = length;
            }

            leftIndex = i;
            rightIndex = j;

            Debug.Assert(leftIndex >= 0 && leftIndex <= length);
            Debug.Assert(rightIndex >= 0 && rightIndex <= length);
        }

        #endregion


        #region squeeze, squeeze!

        [RubyMethod("squeeze")]
        public static MutableString/*!*/ Squeeze(RubyContext/*!*/ context, MutableString/*!*/ self, 
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ args) {
            MutableString result = self.Clone();
            SqueezeMutableString(result, args);
            return result;
        }

        [RubyMethod("squeeze!")]
        public static MutableString/*!*/ SqueezeInPlace(RubyContext/*!*/ context, MutableString/*!*/ self,
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ args) {
            return SqueezeMutableString(self, args);
        }

        private static MutableString SqueezeMutableString(MutableString/*!*/ str, MutableString[]/*!*/ ranges) {
            // if squeezeAll is true then there should be no ranges, and vice versa
            Assert.NotNull(str, ranges);

            // convert the args into a map of characters to be squeezed (same algorithm as count)
            BitArray map = null;
            if (ranges.Length > 0) {
                map = new RangeParser(ranges).Parse();
            }

            // Do the squeeze in place
            int j = 1, k = 1;
            while (j < str.Length) {
                if (str.GetChar(j) == str.GetChar(j-1) && (ranges.Length == 0 || map.Get(str.GetChar(j)))) {
                    j++;
                } else {
                    str.SetChar(k, str.GetChar(j));
                    j++; k++;
                }
            }
            if (j > k) {
                str.Remove(k, j - k);
            }

            // if not modified return null
            return j == k ? null : str;
        }

        #endregion


        #region to_i, hex, oct

        [RubyMethod("to_i")]
        public static object/*!*/ ToInteger(MutableString/*!*/ self, [DefaultProtocol, DefaultParameterValue(10)]int @base) {
            return ClrString.ToInteger(self.ConvertToString(), @base);
        }

        [RubyMethod("hex")]
        public static object/*!*/ ToIntegerHex(MutableString/*!*/ self) {
            return ClrString.ToIntegerHex(self.ConvertToString());
        }

        [RubyMethod("oct")]
        public static object/*!*/ ToIntegerOctal(MutableString/*!*/ self) {
            return ClrString.ToIntegerOctal(self.ConvertToString());
        }

        #endregion

        #region to_f, to_s, to_str, to_clr_string, to_sym, intern

        [RubyMethod("to_f")]
        public static double ToDouble(MutableString/*!*/ self) {
            return ClrString.ToDouble(self.ConvertToString());
        }

        [RubyMethod("to_s")]
        [RubyMethod("to_str")]
        public static MutableString/*!*/ ToS(MutableString/*!*/ self) {
            return self.GetType() == typeof(MutableString) ? self : MutableString.Create(self).TaintBy(self);
        }

        [RubyMethod("to_clr_string")]
        public static string/*!*/ ToClrString(MutableString/*!*/ str) {
            return str.ConvertToString();
        }

        
        [RubyMethod("to_sym")]
        [RubyMethod("intern")]
        public static RubySymbol/*!*/ ToSymbol(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return context.CreateSymbol(self);
        }

        #endregion


        #region upto

        [RubyMethod("upto")]
        public static object UpTo(
            ConversionStorage<MutableString>/*!*/ stringCast, 
            RespondToStorage/*!*/ respondToStorage,
            BinaryOpStorage/*!*/ comparisonStorage,
            BinaryOpStorage/*!*/ lessThanStorage,
            BinaryOpStorage/*!*/ greaterThanStorage,
            BinaryOpStorage/*!*/ equalsStorage,
            UnaryOpStorage/*!*/ succStorage,
            BlockParam block, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ endString) {

            RangeOps.Each(stringCast, respondToStorage, comparisonStorage, lessThanStorage, greaterThanStorage, equalsStorage, succStorage,  
                block, new Range(self, endString, false)
            );

            return self;
        }

        #endregion


        #region replace, reverse, reverse!

        [RubyMethod("replace")]
        public static MutableString/*!*/ Replace(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            // Handle case where objects are the same identity
            if (ReferenceEquals(self, other)) {
                return self;
            }

            self.Clear();
            self.Append(other);
            return self.TaintBy(other);
        }

        [RubyMethod("reverse")]
        public static MutableString/*!*/ GetReversed(MutableString/*!*/ self) {
            return self.Clone().Reverse();
        }

        [RubyMethod("reverse!")]
        public static MutableString/*!*/ Reverse(MutableString/*!*/ self) {
            if (self.Encoding.IsKCoding) {
                throw new NotImplementedException("TODO: KCODE");
            }

            if (self.Length == 0) {
                return self;
            }
            self.RequireNotFrozen();

            // TODO: MRI 1.9: allows invalid characters
            return self.Reverse();
        }

        #endregion


        #region tr, tr_s

        internal static MutableString/*!*/ Translate(MutableString/*!*/ src, MutableString/*!*/ from, MutableString/*!*/ to, 
            bool inplace, bool squeeze, out bool anyCharacterMaps) {
            Assert.NotNull(src, from, to);

            if (from.IsEmpty) {
                anyCharacterMaps = false;
                return inplace ? src : src.Clone();
            }

            MutableString dst;
            if (inplace) {
                dst = src;
            } else {
                dst = src.CreateInstance().TaintBy(src);
            }

            // TODO: KCODE
            src.RequireCompatibleEncoding(from);
            dst.RequireCompatibleEncoding(to);
            from.SwitchToCharacters();
            to.SwitchToCharacters();

            CharacterMap map = CharacterMap.Create(from, to);

            if (to.IsEmpty) {
                anyCharacterMaps = MutableString.TranslateRemove(src, dst, map);
            } else if (squeeze) {
                anyCharacterMaps = MutableString.TranslateSqueeze(src, dst, map);
            } else {
                anyCharacterMaps = MutableString.Translate(src, dst, map);
            }

            return dst;
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr")]
        public static MutableString/*!*/ GetTranslated(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {
            bool _;
            return Translate(self, from, to, false, false, out _);
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr!")]
        public static MutableString Translate(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {

            bool anyCharacterMaps;
            self.RequireNotFrozen();
            Translate(self, from, to, true, false, out anyCharacterMaps);
            return anyCharacterMaps ? self : null;
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr_s")]
        public static MutableString/*!*/ TrSqueeze(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {
            bool _;
            return Translate(self, from, to, false, true, out _);
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr_s!")]
        public static MutableString TrSqueezeInPlace(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {

            bool anyCharacterMaps;
            self.RequireNotFrozen();
            Translate(self, from, to, true, true, out anyCharacterMaps);
            return anyCharacterMaps ? self : null;
        }

        #endregion

        
        #region ljust

        [RubyMethod("ljust")]
        public static MutableString/*!*/ LeftJustify(MutableString/*!*/ self, [DefaultProtocol]int width) {
            // TODO: is this correct? Is it just a space or is this some configurable whitespace thing?
            return LeftJustify(self, width, _DefaultPadding);
        }

        [RubyMethod("ljust")]
        public static MutableString/*!*/ LeftJustify(MutableString/*!*/ self, 
            [DefaultProtocol]int width, [DefaultProtocol, NotNull]MutableString/*!*/ padding) {

            if (padding.Length == 0) {
                throw RubyExceptions.CreateArgumentError("zero width padding");
            }

            int count = width - self.Length;
            if (count <= 0) {
                return self;
            }

            int iterations = count / padding.Length;
            int remainder = count % padding.Length;
            MutableString result = self.Clone().TaintBy(padding);

            for (int i = 0; i < iterations; i++) {
                result.Append(padding);
            }

            result.Append(padding, 0, remainder);

            return result;
        }

        #endregion

        #region rjust

        [RubyMethod("rjust")]
        public static MutableString/*!*/ RightJustify(MutableString/*!*/ self, [DefaultProtocol]int width) {
            // TODO: is this correct? Is it just a space or is this some configurable whitespace thing?
            return RightJustify(self, width, _DefaultPadding);
        }

        [RubyMethod("rjust")]
        public static MutableString/*!*/ RightJustify(MutableString/*!*/ self, 
            [DefaultProtocol]int width, [DefaultProtocol, NotNull]MutableString/*!*/ padding) {

            if (padding.Length == 0) {
                throw RubyExceptions.CreateArgumentError("zero width padding");
            }

            int count = width - self.Length;
            if (count <= 0) {
                return self;
            }

            int iterations = count / padding.Length;
            int remainder = count % padding.Length;
            MutableString result = self.CreateInstance().TaintBy(self).TaintBy(padding);

            for (int i = 0; i < iterations; i++) {
                result.Append(padding);
            }

            result.Append(padding.GetSlice(0, remainder));
            result.Append(self);

            return result;
        }

        #endregion

        #region unpack

        [RubyMethod("unpack")]
        public static RubyArray/*!*/ Unpack(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format) {
            return RubyEncoder.Unpack(self, format);
        }

        #endregion

        #region sum

        [RubyMethod("sum")]
        public static object GetChecksum(MutableString/*!*/ self, [DefaultProtocol, DefaultParameterValue(16)]int bitCount) {
            int length = self.GetByteCount();
            uint mask = (bitCount > 31) ? 0xffffffff : (1U << bitCount) - 1;
            uint sum = 0;
            for (int i = 0; i < length; i++) {
                byte b = self.GetByte(i);
                try {
                    checked { sum = (sum + b) & mask; }
                } catch (OverflowException) {
                    return GetBigChecksum(self, i, sum, bitCount);
                }
            }

            return (sum > Int32.MaxValue) ? (BigInteger)sum : (object)(int)sum;
        }

        private static BigInteger GetBigChecksum(MutableString/*!*/ self, int start, BigInteger/*!*/ sum, int bitCount) {
            BigInteger mask = (((BigInteger)1) << bitCount) - 1;

            int length = self.GetByteCount();
            for (int i = start; i < length; i++) {
                sum = (sum + self.GetByte(i)) & mask;
            }
            return sum;
        }

        #endregion

        #region Encodings (1.9)

        //ascii_only?
        //bytes
        //bytesize
        //chars
        //codepoints
        //each_byte
        //each_char
        //each_codepoint
        //encode
        //encode!
        //encoding
        //force_encoding
        //getbyte
        //setbyte
        //valid_encoding?

        #endregion

        private static void RequireNoVersionChange(MutableString/*!*/ self) {
            if (self.HasChanged) {
                throw RubyExceptions.CreateRuntimeError("string modified");
            }
        }
    }
}
