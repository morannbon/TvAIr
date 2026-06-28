using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace TvAIr.Schedule
{
    /// <summary>
    /// v0.3.33: ユーザー明示チェーンを「単発予約」ではなく「予約群」として扱う共通ロック管理。
    /// ALLOC_ROUTE/TUNER_ALLOC から安全に呼べるよう static facade も持つ。
    /// </summary>
    internal sealed class ChainGroupLockManager
    {
        private readonly Dictionary<string, string> _chainRootToLockedTuner = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _reservationToLockedTuner = new(StringComparer.OrdinalIgnoreCase);

        public void Rebuild(IEnumerable<object?> reservations, Action<string>? log = null)
        {
            _chainRootToLockedTuner.Clear();
            _reservationToLockedTuner.Clear();

            var list = reservations.Where(r => r != null).Cast<object>().ToList();
            var active = list.Where(r => !IsCancelled(r)).ToList();

            foreach (var r in active)
            {
                if (!IsUserChain(r)) continue;

                var id = GetId(r);
                var root = GetChainRoot(r);
                var prev = GetChainPrev(r);
                var tuner = GetTuner(r);

                if (string.IsNullOrWhiteSpace(root))
                    root = !string.IsNullOrWhiteSpace(prev) ? prev : id;

                if (string.IsNullOrWhiteSpace(tuner) || tuner == "-")
                    tuner = ResolveFromPredecessor(active, prev);

                if (string.IsNullOrWhiteSpace(tuner) || tuner == "-")
                    continue;

                if (!_chainRootToLockedTuner.ContainsKey(root))
                    _chainRootToLockedTuner[root] = tuner;

                _reservationToLockedTuner[id] = _chainRootToLockedTuner[root];

                log?.Invoke($"CHAIN_GROUP_LOCK_REBUILD id={id} root={root} prev={prev} lockTuner={_chainRootToLockedTuner[root]}");
            }
        }

        public string? GetLockedTuner(object? reservation)
        {
            if (reservation == null) return null;
            if (IsCancelled(reservation)) return null;
            if (!IsUserChain(reservation)) return null;

            var id = GetId(reservation);
            if (!string.IsNullOrWhiteSpace(id) && _reservationToLockedTuner.TryGetValue(id, out var t1))
                return t1;

            var root = GetChainRoot(reservation);
            if (!string.IsNullOrWhiteSpace(root) && _chainRootToLockedTuner.TryGetValue(root, out var t2))
                return t2;

            return null;
        }

        public bool ApplyLockIfNeeded(object? reservation, Action<string>? log = null)
        {
            if (reservation == null) return false;

            var lockTuner = GetLockedTuner(reservation);
            if (string.IsNullOrWhiteSpace(lockTuner)) return false;

            var current = GetTuner(reservation);
            var id = GetId(reservation);

            if (current != lockTuner)
            {
                SetString(reservation, "TunerName", lockTuner);
                SetString(reservation, "Tuner", lockTuner);
                SetString(reservation, "tuner", lockTuner);
                SetBool(reservation, "Conflicted", false);
                SetBool(reservation, "conflicted", false);
                log?.Invoke($"CHAIN_GROUP_LOCK_APPLIED id={id} from={current} to={lockTuner}");
                return true;
            }

            log?.Invoke($"CHAIN_GROUP_LOCK_KEEP id={id} tuner={lockTuner}");
            return false;
        }

        public bool IsLockedToDifferentTuner(object? reservation, string? candidateTuner)
        {
            var locked = GetLockedTuner(reservation);
            if (string.IsNullOrWhiteSpace(locked)) return false;
            if (string.IsNullOrWhiteSpace(candidateTuner)) return false;
            return !string.Equals(locked, candidateTuner, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveFromPredecessor(IReadOnlyList<object> active, string prev)
        {
            if (string.IsNullOrWhiteSpace(prev)) return string.Empty;
            var p = active.FirstOrDefault(x => string.Equals(GetId(x), prev, StringComparison.OrdinalIgnoreCase));
            return p == null ? string.Empty : GetTuner(p);
        }

        internal static bool IsUserChain(object obj)
        {
            return GetBool(obj, "UserChain")
                || GetBool(obj, "userChain")
                || !string.IsNullOrWhiteSpace(GetChainPrev(obj));
        }

        internal static bool IsCancelled(object obj)
        {
            var status = GetString(obj, "Status");
            if (string.IsNullOrWhiteSpace(status)) status = GetString(obj, "status");
            return status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("キャンセル", StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetId(object obj)
        {
            var s = GetString(obj, "Id");
            if (string.IsNullOrWhiteSpace(s)) s = GetString(obj, "id");
            if (s.StartsWith("R", StringComparison.OrdinalIgnoreCase)) return s;
            return string.IsNullOrWhiteSpace(s) ? string.Empty : "R" + s;
        }

        internal static string GetTuner(object obj)
        {
            var s = GetString(obj, "TunerName");
            if (string.IsNullOrWhiteSpace(s)) s = GetString(obj, "Tuner");
            if (string.IsNullOrWhiteSpace(s)) s = GetString(obj, "tuner");
            return s;
        }

        internal static string GetChainPrev(object obj)
        {
            var s = GetString(obj, "ChainPrev");
            if (string.IsNullOrWhiteSpace(s)) s = GetString(obj, "chainPrev");
            if (string.IsNullOrWhiteSpace(s)) s = GetString(obj, "PredecessorId");
            return s;
        }

        internal static string GetChainRoot(object obj)
        {
            var s = GetString(obj, "ChainRoot");
            if (string.IsNullOrWhiteSpace(s)) s = GetString(obj, "chainRoot");
            return s;
        }

        internal static bool GetBool(object obj, string name)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null) return false;
                var v = p.GetValue(obj);
                return v is bool b && b;
            }
            catch { return false; }
        }

        internal static string GetString(object obj, string name)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var v = p?.GetValue(obj);
                return v?.ToString() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        internal static void SetString(object obj, string name, string value)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                    p.SetValue(obj, value);
            }
            catch { }
        }

        internal static void SetBool(object obj, string name, bool value)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                    p.SetValue(obj, value);
            }
            catch { }
        }
    }

    /// <summary>
    /// 既存ALLOC_ROUTE/TUNER_ALLOCから少ない変更で呼ぶための共通facade。
    /// </summary>
    internal static class ChainGroupLockRoute
    {
        private static readonly object Gate = new();
        private static ChainGroupLockManager _manager = new();

        public static void Rebuild(IEnumerable<object?> reservations, Action<string>? log = null)
        {
            lock (Gate)
            {
                _manager = new ChainGroupLockManager();
                _manager.Rebuild(reservations, log);
            }
        }

        public static bool Apply(object? reservation, Action<string>? log = null)
        {
            lock (Gate)
            {
                return _manager.ApplyLockIfNeeded(reservation, log);
            }
        }

        public static bool IsCandidateBlocked(object? reservation, string? candidateTuner)
        {
            lock (Gate)
            {
                return _manager.IsLockedToDifferentTuner(reservation, candidateTuner);
            }
        }

        public static void LogIfChain(object? reservation, Action<string>? log = null, string stage = "CHECK")
        {
            if (reservation == null) return;
            try
            {
                if (!ChainGroupLockManager.IsUserChain(reservation)) return;
                var id = ChainGroupLockManager.GetId(reservation);
                var tuner = ChainGroupLockManager.GetTuner(reservation);
                log?.Invoke($"CHAIN_GROUP_LOCK_{stage} id={id} tuner={tuner}");
            }
            catch { }
        }
    }
}
