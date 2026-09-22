using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// PREV / NEXT with a position readout, for lists that are taller than their panel.
    ///
    /// Paging rather than scrolling, deliberately. A ScrollRect needs a viewport, a mask and a
    /// content rect sized from the rows, and the rows here are absolutely positioned - so it would
    /// mean rebuilding both screens' layout around a component whose behaviour (inertia, scrollbar
    /// styling, wheel capture) then has to be made to match a client that uses buttons for this in
    /// its own deck lists. Two buttons cost nothing and look native.
    ///
    /// The page is CLAMPED on every read rather than when the list changes, because both screens
    /// repopulate from data that can shrink underneath them - a match ends, the board refreshes -
    /// and a stale page index would otherwise show an empty list with no way back.
    /// </summary>
    internal class Pager
    {
        private readonly int _perPage;
        private int _page;

        public Pager(int perPage) { _perPage = Mathf.Max(1, perPage); }

        public int PerPage { get { return _perPage; } }

        /// <summary>Page count for a list of this size, never less than one.</summary>
        public int PageCount(int total)
        {
            return Mathf.Max(1, Mathf.CeilToInt(total / (float)_perPage));
        }

        /// <summary>Current page, clamped to what the list can actually offer.</summary>
        public int Page(int total)
        {
            _page = Mathf.Clamp(_page, 0, PageCount(total) - 1);
            return _page;
        }

        /// <summary>Index of the first item on the current page.</summary>
        public int Start(int total) { return Page(total) * _perPage; }

        /// <summary>How many items this page holds.</summary>
        public int Count(int total) { return Mathf.Clamp(total - Start(total), 0, _perPage); }

        public bool CanPrev(int total) { return Page(total) > 0; }
        public bool CanNext(int total) { return Page(total) < PageCount(total) - 1; }

        public bool Move(int delta, int total)
        {
            int before = Page(total);
            _page = Mathf.Clamp(before + delta, 0, PageCount(total) - 1);
            return _page != before;
        }

        public void Reset() { _page = 0; }

        /// <summary>"10-18 of 42", or empty when everything fits on one page.</summary>
        public string Label(int total)
        {
            if (total <= _perPage) return "";
            int from = Start(total) + 1, to = Start(total) + Count(total);
            return from + "-" + to + " of " + total;
        }
    }
}
