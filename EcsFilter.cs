﻿using System.Collections.Generic;

namespace ECS
{
    public struct EcsFilter
    {
        public BitMask Includes;
        public BitMask Excludes;
        //TODO: implement sortable groups
        private Dictionary<int, int> _filteredEntities;
        private SimpleVector<int> _entitiesVector;
        private HashSet<int> _addSet;
        private HashSet<int> _removeSet;

        public delegate void IteartionDelegate(int[] entities, int count);

        public int Length => _entitiesVector.Length;
        public int this[int i] => _entitiesVector[i];

        private class BoxedInt { public int Value = 0; }
        private BoxedInt _lockCount;

        private int? _cachedHash;
        public int HashCode
        {
            get
            {
                if (!_cachedHash.HasValue)
                    _cachedHash = GetHashFromMasks(Includes, Excludes);
                return _cachedHash.Value;
            }
        }

        public static int GetHashFromMasks(in BitMask includes, in BitMask excludes)
        {
            int hash = 17;
            var nextSetBit = includes.GetNextSetBit(0);
            while (nextSetBit != -1)
            {
                hash = hash * 23 + nextSetBit.GetHashCode();
                nextSetBit = includes.GetNextSetBit(nextSetBit + 1);
            }

            nextSetBit = excludes.GetNextSetBit(0);
            while (nextSetBit != -1)
            {
                hash = hash * 23 + nextSetBit.GetHashCode();
                nextSetBit = excludes.GetNextSetBit(nextSetBit + 1);
            }

            return hash;
        }

        public EcsFilter(in BitMask includes, in BitMask excludes, bool dummy = false)
        {
            Includes = default;
            Includes.Copy(includes);
            Excludes = default;
            Excludes.Copy(excludes);
            _filteredEntities = dummy ? null : new Dictionary<int, int>(EcsCacheSettings.FilteredEntitiesSize);
            _addSet = dummy ? null : new HashSet<int>();
            _removeSet = dummy ? null : new HashSet<int>();
            _cachedHash = GetHashFromMasks(Includes, Excludes);
            _lockCount = dummy ? null : new BoxedInt();
            _entitiesVector = dummy ? null : new SimpleVector<int>(EcsCacheSettings.FilteredEntitiesSize);
        }

        public void Iterate(IteartionDelegate iteartionDelegate)
        {
            _lockCount.Value++;
            iteartionDelegate(_entitiesVector._elements, _entitiesVector._end);
            _lockCount.Value--;
#if DEBUG
            if (_lockCount.Value < 0)
                throw new EcsException("_lockCount shouldn't be negative");
            else
#endif
            if (_lockCount.Value == 0)
            {
                foreach (var id in _addSet)
                {
                    if (!_filteredEntities.ContainsKey(id))
                    {
                        _entitiesVector.Add(id);
                        _filteredEntities.Add(id, _entitiesVector._end - 1);
                    }
                }
                _addSet.Clear();
                foreach (var id in _removeSet)
                {
                    if (_filteredEntities.TryGetValue(id, out int idx))
                    {
                        _entitiesVector.Remove(idx);
                        var end = _entitiesVector._end;
                        if (idx < end)
                        {
                            _filteredEntities[idx] = _filteredEntities[end];
                            _filteredEntities.Remove(end);
                        }
                        else
                            _filteredEntities.Remove(id);
                    }
                }
                _removeSet.Clear();
            }
        }

        public void Add(int id)
        {
            if (_lockCount.Value > 0)
                _addSet.Add(id);
            else if (!_filteredEntities.ContainsKey(id))
            {
                _entitiesVector.Add(id);
                _filteredEntities.Add(id, _entitiesVector._end - 1);
            }
        }

        public void Remove(int id)
        {
            if (_lockCount.Value > 0)
                _removeSet.Add(id);
            else if (_filteredEntities.TryGetValue(id, out int idx))
            {
                _entitiesVector.Remove(idx);
                var end = _entitiesVector._end;
                if (idx < end)
                {
                    _filteredEntities[idx] = _filteredEntities[end];
                    _filteredEntities.Remove(end);
                }
                else
                    _filteredEntities.Remove(id);
            }
        }

        public void Copy(in EcsFilter other)
        {
            Includes.Copy(other.Includes);
            Excludes.Copy(other.Excludes);

            if (_filteredEntities != null)
                _filteredEntities.Clear();
            else
                _filteredEntities = new Dictionary<int, int>(EcsCacheSettings.FilteredEntitiesSize);
            foreach (var entity in other._filteredEntities)
                _filteredEntities.Add(entity.Key, entity.Value);

            _lockCount = other._lockCount;
        }
    }
}
