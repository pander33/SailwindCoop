namespace SailwindCoop.Sync
{
    internal struct InstrumentRequestEpoch
    {
        private uint _actor, _request, _revision;

        internal void Observe(uint revision, uint actor, uint request)
        {
            if (_actor == actor && _request == request) return;
            _actor = actor; _request = request; _revision = revision;
        }

        // The pre-ack final belongs only to the revision produced by that action.
        // A retained acknowledgement cannot authorize it after another root change.
        internal bool Accept(uint parent, uint current, uint actor, uint request)
            => unchecked((int)(parent - current)) >= 0 ||
               (request != 0 && actor == _actor && request == _request && current == _revision);
    }
}
