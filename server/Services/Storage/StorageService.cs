// Module: Storage - owner group: STORAGE (Storage)
// Seshat profile storage: one JSON document per account, the game's only progression store.
// Protocol: verify-C3 (normative: status codes, ETag, merge, selectors); report 03; report 02 §9.
// Spec of what we answer and why: docs/protocol/storage.md.

using System.Globalization;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// Seshat profile storage: one JSON document per account, the game's only progression store (verify-C3 §1).
    /// <para>
    /// Pieces: <see cref="StorageEndpoints"/> (HTTP: auth, credential resolution, routes) →
    /// <see cref="IProfileStore"/> (chosen per account by an optional <see cref="IProfileStoreLocator"/>; default
    /// <see cref="LocalProfileStore"/>: files, FIFO per-account lock, backups) → <see cref="SeshatOperations"/>
    /// (set/merge/delete semantics) over <see cref="ProfileJson"/> trees whose set values are kept byte-for-byte
    /// (<see cref="VerbatimJson"/>).
    /// </para>
    /// Features provided to other modules: <see cref="IProfileAccess"/> (read/write profiles, the IAP inventory
    /// grant channel) and <see cref="LocalProfileStore"/>. Events: <see cref="LocalProfileStore.ChangedTopic"/>,
    /// <see cref="ProfileAccess.LedgerTopic"/>.
    /// </summary>
    public static class StorageService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            var log = context.Logger.For("storage");
            var settings = new StorageSettings(context.Settings);
            var store = new LocalProfileStore(context.GetDataPath("storage"), context.Clock, log, settings, context.Accounts, context.Events);
            var blobs = new DataBlobStore(Path.Combine(store.RootDirectory, "data"), store.Locks, context.Clock);

            context.Features.Set<LocalProfileStore>(store);
            context.Features.Set<IProfileAccess>(new ProfileAccess(store, context.Events));

            new StorageEndpoints(context, store, blobs, log).Register(router);
            log.Info("profiles in " + store.ProfilesDirectory + " (" + store.AccountIds().Count.ToString(CultureInfo.InvariantCulture) + " stored)");
        }
    }
}
