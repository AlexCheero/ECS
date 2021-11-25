﻿//TODO: cover with tests
using System;
using System.Collections.Generic;

namespace ECS
{
    class FiltersCollection
    {
        class FilterEqComparer : IEqualityComparer<EcsFilter>
        {
            //comps in this method should always be sorted
            //and, if equals, their length should alway be equal, because it will throw in Filter's
            //ctor, where the cache is calculated, if array contains duplicated elements
            private bool ComponentsEquals(Type[] compsA, Type[] compsB)
            {
                if (compsA == null)
                    return compsB == null;
                if (compsB == null)
                    return false;
                if (compsA.Length != compsB.Length)
                    return false;

                for (int i = 0; i < compsA.Length; i++)
                {
                    if (compsA[i] != compsB[i])
                        return false;
                }

                return true;
            }

            public bool Equals(EcsFilter x, EcsFilter y)
            {
                bool compsEq = ComponentsEquals(x.Comps, y.Comps);
                bool excludesEq = ComponentsEquals(x.Excludes, y.Excludes);
                return compsEq && excludesEq;
            }

            public int GetHashCode(EcsFilter filter) => filter.HashCode;
        }

        private HashSet<EcsFilter> _set;
        private List<EcsFilter> _list;

        private static FilterEqComparer _filterComparer;

        static FiltersCollection()
        {
            _filterComparer = new FilterEqComparer();
        }

        public int Length => _list.Count;

        public EcsFilter this[int i]
        {
            get
            {
#if DEBUG
                if (i >= _list.Count)
                    throw new EcsException("wrong filter index");
#endif
                return _list[i];
            }
        }

        //all prealloc should be performed only for world's copies
        public FiltersCollection(int prealloc = 0)
        {
            _set = new HashSet<EcsFilter>(_filterComparer);
            _list = new List<EcsFilter>(prealloc);
            for (int i = 0; i < prealloc; i++)
                _list.Add(new EcsFilter { FilteredEntities = new HashSet<int>() });
        }

        //all adding should be preformed only for initial world
        public bool TryAdd(ref Type[] comps, ref Type[] excludes, out int idx)
        {
            var dummy = new EcsFilter(EcsFilter.GetHashFromComponents(comps, excludes));
            //TODO: make proper define
#if UNITY
            var addNew = !_set.Contains(dummy);
#else

            var addNew = !_set.TryGetValue(dummy, out dummy);
#endif
            if (addNew)
            {
                var newFilter = new EcsFilter(comps, excludes, new HashSet<int>());
                _set.Add(newFilter);
                _list.Add(newFilter);
                idx = _list.Count - 1;

#if DEBUG
                if (_list.Count != _set.Count)
                    throw new EcsException("FiltersCollection.TryAdd _set _list desynch");
#endif
                return true;
            }
            else
            {
                //TODO: make proper define
#if UNITY
                foreach (var filter in _set)
                {
                    if (!_filterComparer.Equals(dummy, filter))
                        continue;
                    dummy = filter;
                    break;
                }
#endif

                //TODO: probably should force GC after adding all filters to remove duplicates
                comps = dummy.Comps;
                excludes = dummy.Excludes;
                idx = _list.IndexOf(dummy);
                return false;
            }
        }

        public void RemoveId(int id)
        {
            foreach (var filter in _set)
            {
                if (filter.FilteredEntities.Contains(id))
                    filter.FilteredEntities.Remove(id);
            }
        }

        public void Copy(FiltersCollection other)
        {
#if DEBUG
            if (_list.Count != other._list.Count)
                throw new EcsException("FiltersCollection lists should have same size");
#endif

            _set.Clear();
            for (int i = 0; i < other._list.Count; i++)
            {
                var filterCopy = _list[i];
                var otherFilter = other._list[i];
                filterCopy.Comps = otherFilter.Comps;
                filterCopy.Excludes = otherFilter.Excludes;
                filterCopy.FilteredEntities.Clear();
                foreach (var entity in otherFilter.FilteredEntities)
                    filterCopy.FilteredEntities.Add(entity);
                _list[i] = filterCopy;
                _set.Add(filterCopy);
            }
        }
    }
}
