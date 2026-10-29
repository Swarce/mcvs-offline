// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// The JNI calls are Android only (MCVS_ANDROID); the outcomes of the bounded UI-thread waits are platform-independent
// (compiled into the net8 tests).
using System;
using McvsOffline.Server.Services.Admin;
#if MCVS_ANDROID
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Runtime;
#endif

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// How a file or a text leaves the phone from the settings page (design §8.6, D4: the in-game WebView has no downloads and
    /// no file chooser), the clipboard and toasts, all over the JNI table (<see cref="AndroidJni"/>):
    /// <list type="bullet">
    /// <item>Android 10+: a MediaStore Downloads row (<c>Download/MCVS-Offline/&lt;name&gt;</c>, <c>is_pending</c> 1 → write
    /// through the row's file descriptor → 0, which scans it: the real <c>_size</c>), recorded in the
    /// <see cref="MediaStoreLedger"/>; then, unless the subject is null ("Save to Downloads"), <c>ACTION_SEND</c> with the content
    /// URI (<c>EXTRA_STREAM</c>, ClipData, <c>FLAG_GRANT_READ_URI_PERMISSION</c>) in a chooser started from the game's activity on
    /// the UI thread. MediaProvider serves the URI to the share target even while the game is frozen in the background.</item>
    /// <item>Android 7–9: <c>file://</c> of the file under Android/data with the StrictMode VM policy relaxed once (the
    /// FileUriExposed check; target SDK 29, invariant I1); Android 5–6: <c>file://</c> directly. Receivers need storage
    /// permission (best effort, device check C15).</item>
    /// <item>Text: the game's own <c>SUtils.shareInfo(subject, text, title)</c>; clipboard: <c>ClipData.newPlainText</c> →
    /// <c>ClipboardManager.setPrimaryClip</c>; toast: <c>Toast.makeText(context, text, LENGTH_LONG).show()</c>; each on the UI thread.</item>
    /// </list>
    /// Every method deletes its JNI local references, catches everything and returns a failure result. The UI-thread waits are
    /// bounded (the chooser 3 s, text and clipboard <see cref="QuickUiWaitMs"/>); what the UI thread has not started by then is
    /// withdrawn (<see cref="UiCall"/>), so no sheet opens and no copy lands after the page was told it failed.
    /// </summary>
    internal static class AndroidShare
    {
        public const string RelativePath = "Download/MCVS-Offline/";

        /// <summary>
        /// ShareText and CopyToClipboard: IAdminHost members other than ShareFile, WaitForGameLoop and Exit return within a second,
        /// so at most 800 ms plus <see cref="UiCall.StartedGraceMs"/>; a call the UI thread has not started by then is withdrawn.
        /// </summary>
        public const int QuickUiWaitMs = 800;

        /// <summary>The share result of SUtils.shareInfo on the UI thread (<see cref="UiCall"/> outcome).</summary>
        public static AdminShareResult TextShareResult(int outcome)
        {
            if (outcome == UiCall.Done) return new AdminShareResult { Ok = true, Method = "text", Message = "Choose an app." };
            // Started, not ended in time: it runs to its end on the UI thread, the sheet may still open (the share lease stays).
            if (outcome == UiCall.Pending)
                return new AdminShareResult { Ok = false, Method = "text", Message = "The share sheet is slow to open; it may still appear." };
            if (outcome == UiCall.Withdrawn) return AdminShareResult.None("The game is busy, so the share sheet did not open. Try again.");
            return AdminShareResult.None("The share sheet did not open.");
        }

        /// <summary>The clipboard copy's outcome: true only once it ran (not ended in time is unknown, so false).</summary>
        public static bool ClipboardResult(int outcome)
        {
            return outcome == UiCall.Done;
        }

#if MCVS_ANDROID
        private const int FlagGrantReadUriPermission = 1;

        /// <summary>The chooser of ShareFile (IAdminHost allows ShareFile 10 s).</summary>
        private const int UiWaitMs = 3000;
        private static int strictModeRelaxed;

        public static AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text,
                                                 MediaStoreLedger ledger, Action<string, Exception> warn)
        {
            int sdk = AndroidJni.SdkInt;
            if (sdk >= 29) return ShareThroughMediaStore(path, mimeType, displayName, subject, text, ledger, warn);
            if (subject == null) return AdminShareResult.None("Saving to Downloads needs Android 10 or later. Use Share instead.");
            return ShareFileUri(path, mimeType, subject, text, sdk, warn);
        }

        // ------------------------------------------------------------------ Android 10+: MediaStore Downloads

        private static AdminShareResult ShareThroughMediaStore(string path, string mimeType, string displayName, string subject, string text,
                                                              MediaStoreLedger ledger, Action<string, Exception> warn)
        {
            IntPtr resolver = IntPtr.Zero, uri = IntPtr.Zero;
            try
            {
                resolver = AndroidJni.CallObject(AndroidJni.AppContext(), AndroidJni.GetContentResolver);
                uri = Insert(resolver, displayName, mimeType);
                if (uri == IntPtr.Zero) return AdminShareResult.None("Android did not create the file in Downloads.");
                bool written = false;
                try
                {
                    WriteFile(resolver, uri, path);
                    SetPending(resolver, uri, 0);
                    written = true;
                }
                finally
                {
                    if (!written) DeleteRow(resolver, uri, warn);
                }
                string uriText = AndroidJni.TakeString(AndroidJni.CallObject(uri, AndroidJni.UriToString));
                string name = DisplayName(resolver, uri) ?? displayName;
                if (ledger != null)
                {
                    IntPtr r = resolver;
                    ledger.Record(uriText, name, DateTime.UtcNow, u => DeleteRow(r, u, warn));
                }
                string publicPath = RelativePath + name;
                if (subject == null)
                    return new AdminShareResult { Ok = true, Method = "downloads", PublicPath = publicPath, Message = "Saved to Downloads > MCVS-Offline > " + name + "." };
                if (!StartChooser(uri, mimeType, subject, text, warn))
                    return new AdminShareResult { Ok = false, Method = "downloads", PublicPath = publicPath,
                                                  Message = "Saved to Downloads > MCVS-Offline > " + name + ", but the share sheet did not open." };
                return new AdminShareResult { Ok = true, Method = "sheet", PublicPath = publicPath,
                                              Message = "Choose an app. A copy is in Downloads > MCVS-Offline > " + name + "." };
            }
            catch (Exception ex)
            {
                warn("admin: sharing through MediaStore failed", ex);
                return AdminShareResult.None("The file could not be shared.");
            }
            finally
            {
                AndroidJni.Delete(uri);
                AndroidJni.Delete(resolver);
            }
        }

        /// <summary>ContentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, {name, type, Download/MCVS-Offline/, pending}).</summary>
        private static IntPtr Insert(IntPtr resolver, string displayName, string mimeType)
        {
            IntPtr values = IntPtr.Zero, downloads = IntPtr.Zero;
            try
            {
                values = AndroidJni.New(AndroidJni.ValuesNew);
                PutString(values, "_display_name", displayName);
                PutString(values, "mime_type", mimeType);
                PutString(values, "relative_path", RelativePath);
                PutInt(values, "is_pending", 1);
                downloads = AndroidJni.GetStaticObject(AndroidJni.DownloadsUri);
                return AndroidJni.CallObject(resolver, AndroidJni.ResolverInsert, new JValue(downloads), new JValue(values));
            }
            finally
            {
                AndroidJni.Delete(downloads);
                AndroidJni.Delete(values);
            }
        }

        /// <summary>openFileDescriptor(uri, "w") → getFd() → libc write loop → close().</summary>
        private static void WriteFile(IntPtr resolver, IntPtr uri, string path)
        {
            IntPtr mode = AndroidJni.Str("w");
            IntPtr pfd = IntPtr.Zero;
            try
            {
                pfd = AndroidJni.CallObject(resolver, AndroidJni.ResolverOpenFd, new JValue(uri), new JValue(mode));
                if (pfd == IntPtr.Zero) throw new IOException("no file descriptor for the Downloads row");
                bool closed = false;
                try
                {
                    int fd = AndroidJni.CallInt(pfd, AndroidJni.PfdGetFd);
                    if (fd < 0) throw new IOException("invalid file descriptor");
                    var buffer = new byte[64 * 1024];
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0) WriteAll(fd, buffer, n);
                    }
                    AndroidJni.CallVoid(pfd, AndroidJni.PfdClose);   // closes the descriptor
                    closed = true;
                }
                finally
                {
                    if (!closed)
                    {
                        try { AndroidJni.CallVoid(pfd, AndroidJni.PfdClose); } catch (Exception) { }
                    }
                }
            }
            finally
            {
                AndroidJni.Delete(pfd);
                AndroidJni.Delete(mode);
            }
        }

        private static void WriteAll(int fd, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                byte[] data = buffer;
                if (offset > 0)
                {
                    // A short write (rare on a regular file): the rest from a copy, since the binding takes the array's start.
                    data = new byte[count - offset];
                    Array.Copy(buffer, offset, data, 0, data.Length);
                }
                long n = Libc.write(fd, data, new IntPtr(count - offset)).ToInt64();
                if (n < 0)
                {
                    int errno = Marshal.GetLastWin32Error();
                    if (errno == 4) continue;   // EINTR
                    throw new IOException("write failed (errno " + errno + ")");
                }
                if (n == 0) throw new IOException("write wrote nothing");
                offset += (int)n;
            }
        }

        private static void SetPending(IntPtr resolver, IntPtr uri, int pending)
        {
            IntPtr values = IntPtr.Zero;
            try
            {
                values = AndroidJni.New(AndroidJni.ValuesNew);
                PutInt(values, "is_pending", pending);
                AndroidJni.CallInt(resolver, AndroidJni.ResolverUpdate, new JValue(uri), new JValue(values), new JValue(IntPtr.Zero), new JValue(IntPtr.Zero));
            }
            finally
            {
                AndroidJni.Delete(values);
            }
        }

        /// <summary>The row's _display_name (MediaProvider adds " (1)" on a name clash), or null.</summary>
        private static string DisplayName(IntPtr resolver, IntPtr uri)
        {
            IntPtr projection = IntPtr.Zero, cursor = IntPtr.Zero;
            try
            {
                projection = JNIEnv.NewArray(new[] { "_display_name" });
                cursor = AndroidJni.CallObject(resolver, AndroidJni.ResolverQuery, new JValue(uri), new JValue(projection),
                                               new JValue(IntPtr.Zero), new JValue(IntPtr.Zero), new JValue(IntPtr.Zero));
                if (cursor == IntPtr.Zero) return null;
                try
                {
                    if (!AndroidJni.CallBool(cursor, AndroidJni.CursorMoveToFirst)) return null;
                    return AndroidJni.TakeString(AndroidJni.CallObject(cursor, AndroidJni.CursorGetString, new JValue(0)));
                }
                finally
                {
                    AndroidJni.CallVoid(cursor, AndroidJni.CursorClose);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                AndroidJni.Delete(cursor);
                AndroidJni.Delete(projection);
            }
        }

        /// <summary>ContentResolver.delete(Uri.parse(uri), null, null): rows deleted (0: gone already), -1 on failure.</summary>
        private static int DeleteRow(IntPtr resolver, string uriText, Action<string, Exception> warn)
        {
            IntPtr text = IntPtr.Zero, uri = IntPtr.Zero;
            try
            {
                text = AndroidJni.Str(uriText);
                uri = AndroidJni.CallStaticObject(AndroidJni.UriParse, new JValue(text));
                return DeleteRow(resolver, uri, warn);
            }
            catch (Exception ex)
            {
                warn("admin: deleting an old shared file failed", ex);
                return -1;
            }
            finally
            {
                AndroidJni.Delete(uri);
                AndroidJni.Delete(text);
            }
        }

        private static int DeleteRow(IntPtr resolver, IntPtr uri, Action<string, Exception> warn)
        {
            try
            {
                return AndroidJni.CallInt(resolver, AndroidJni.ResolverDelete, new JValue(uri), new JValue(IntPtr.Zero), new JValue(IntPtr.Zero));
            }
            catch (Exception ex)
            {
                warn("admin: deleting a Downloads row failed", ex);
                return -1;
            }
        }

        private static void PutString(IntPtr values, string key, string value)
        {
            IntPtr k = AndroidJni.Str(key), v = AndroidJni.Str(value);
            try
            {
                AndroidJni.CallVoid(values, AndroidJni.ValuesPutString, new JValue(k), new JValue(v));
            }
            finally
            {
                AndroidJni.Delete(v);
                AndroidJni.Delete(k);
            }
        }

        private static void PutInt(IntPtr values, string key, int value)
        {
            IntPtr k = AndroidJni.Str(key), boxed = IntPtr.Zero;
            try
            {
                boxed = AndroidJni.CallStaticObject(AndroidJni.IntegerValueOf, new JValue(value));
                AndroidJni.CallVoid(values, AndroidJni.ValuesPutInteger, new JValue(k), new JValue(boxed));
            }
            finally
            {
                AndroidJni.Delete(boxed);
                AndroidJni.Delete(k);
            }
        }

        // ------------------------------------------------------------------ Android 5-9: file://

        private static AdminShareResult ShareFileUri(string path, string mimeType, string subject, string text, int sdk, Action<string, Exception> warn)
        {
            IntPtr file = IntPtr.Zero, jpath = IntPtr.Zero, uri = IntPtr.Zero;
            try
            {
                if (sdk >= 24) RelaxStrictMode();
                jpath = AndroidJni.Str(path);
                file = AndroidJni.New(AndroidJni.FileNew, new JValue(jpath));
                uri = AndroidJni.CallStaticObject(AndroidJni.UriFromFile, new JValue(file));
                if (!StartChooser(uri, mimeType, subject, text, warn)) return AdminShareResult.None("The share sheet did not open.");
                return new AdminShareResult { Ok = true, Method = "sheet", PublicPath = null, Message = "Choose an app." };
            }
            catch (Exception ex)
            {
                warn("admin: sharing a file:// URI failed", ex);
                return AdminShareResult.None("The file could not be shared.");
            }
            finally
            {
                AndroidJni.Delete(uri);
                AndroidJni.Delete(file);
                AndroidJni.Delete(jpath);
            }
        }

        /// <summary>StrictMode.setVmPolicy(new StrictMode.VmPolicy.Builder().build()) once: no FileUriExposedException (7-9).</summary>
        private static void RelaxStrictMode()
        {
            if (Interlocked.Exchange(ref strictModeRelaxed, 1) != 0) return;
            IntPtr builder = IntPtr.Zero, policy = IntPtr.Zero;
            try
            {
                builder = AndroidJni.New(AndroidJni.VmPolicyBuilderNew);
                policy = AndroidJni.CallObject(builder, AndroidJni.VmPolicyBuild);
                AndroidJni.CallStaticVoid(AndroidJni.StrictSetVmPolicy, new JValue(policy));
            }
            finally
            {
                AndroidJni.Delete(policy);
                AndroidJni.Delete(builder);
            }
        }

        // ------------------------------------------------------------------ the chooser

        /// <summary>ACTION_SEND for uri in a chooser, started from the game's activity on the UI thread.</summary>
        private static bool StartChooser(IntPtr uri, string mimeType, string subject, string text, Action<string, Exception> warn)
        {
            IntPtr intent = IntPtr.Zero, chooser = IntPtr.Zero, global = IntPtr.Zero;
            var locals = new List<IntPtr>();
            try
            {
                intent = AndroidJni.New(AndroidJni.IntentNew, new JValue(Keep(locals, AndroidJni.Str("android.intent.action.SEND"))));
                Keep(locals, AndroidJni.CallObject(intent, AndroidJni.IntentSetType, new JValue(Keep(locals, AndroidJni.Str(mimeType)))));
                Keep(locals, AndroidJni.CallObject(intent, AndroidJni.IntentPutParcelable,
                                                   new JValue(Keep(locals, AndroidJni.Str("android.intent.extra.STREAM"))), new JValue(uri)));
                Keep(locals, AndroidJni.CallObject(intent, AndroidJni.IntentPutString,
                                                   new JValue(Keep(locals, AndroidJni.Str("android.intent.extra.SUBJECT"))),
                                                   new JValue(Keep(locals, AndroidJni.Str(subject)))));
                if (!string.IsNullOrEmpty(text))
                {
                    Keep(locals, AndroidJni.CallObject(intent, AndroidJni.IntentPutString,
                                                       new JValue(Keep(locals, AndroidJni.Str("android.intent.extra.TEXT"))),
                                                       new JValue(Keep(locals, AndroidJni.Str(text)))));
                }
                IntPtr clip = Keep(locals, AndroidJni.CallStaticObject(AndroidJni.ClipNewRawUri, new JValue(Keep(locals, AndroidJni.Str(""))), new JValue(uri)));
                AndroidJni.CallVoid(intent, AndroidJni.IntentSetClipData, new JValue(clip));
                Keep(locals, AndroidJni.CallObject(intent, AndroidJni.IntentAddFlags, new JValue(FlagGrantReadUriPermission)));
                chooser = AndroidJni.CallStaticObject(AndroidJni.IntentCreateChooser, new JValue(intent), new JValue(Keep(locals, AndroidJni.Str(subject))));
                if (chooser == IntPtr.Zero) return false;
                global = JNIEnv.NewGlobalRef(chooser);
                IntPtr target = global;
                int result = AndroidJni.RunOnUiThread(() =>
                {
                    IntPtr activity = IntPtr.Zero;
                    try
                    {
                        activity = AndroidJni.CallStaticObject(AndroidJni.GetActivity);
                        if (activity == IntPtr.Zero) throw new InvalidOperationException("no game activity");
                        AndroidJni.CallVoid(activity, AndroidJni.StartActivity, new JValue(target));
                    }
                    finally
                    {
                        AndroidJni.Delete(activity);
                        AndroidJni.DeleteGlobal(target);   // the UI thread owns it from here
                    }
                }, UiWaitMs, ex => warn("admin: starting the share chooser failed", ex));
                // Never run (not posted, or withdrawn after the wait): the global reference is still ours.
                if (result == AndroidJni.UiNotPosted || result == AndroidJni.UiWithdrawn) AndroidJni.DeleteGlobal(global);
                return result == AndroidJni.UiDone;
            }
            finally
            {
                AndroidJni.Delete(chooser);
                AndroidJni.Delete(intent);
                foreach (IntPtr local in locals) AndroidJni.Delete(local);
            }
        }

        private static IntPtr Keep(List<IntPtr> locals, IntPtr local)
        {
            if (local != IntPtr.Zero) locals.Add(local);
            return local;
        }

        // ------------------------------------------------------------------ text, clipboard, toast

        /// <summary>
        /// The game's own SUtils.shareInfo(subject, text, title) on the UI thread, within <see cref="QuickUiWaitMs"/>: Ok "text"
        /// once it ran; "none" when it failed or never runs (withdrawn); Ok false with Method "text" when it started but had not
        /// ended in time (the share sheet may still open).
        /// </summary>
        public static AdminShareResult ShareText(string subject, string text, Action<string, Exception> warn)
        {
            int result = AndroidJni.RunOnUiThread(() =>
            {
                IntPtr s = AndroidJni.Str(subject), t = AndroidJni.Str(text), title = AndroidJni.Str(subject);
                try
                {
                    AndroidJni.CallStaticVoid(AndroidJni.ShareInfo, new JValue(s), new JValue(t), new JValue(title));
                }
                finally
                {
                    AndroidJni.Delete(title);
                    AndroidJni.Delete(t);
                    AndroidJni.Delete(s);
                }
            }, QuickUiWaitMs, ex => warn("admin: sharing a text failed", ex));
            return TextShareResult(result);
        }

        /// <summary>
        /// ClipboardManager.setPrimaryClip(ClipData.newPlainText(label, text)) on the UI thread, within <see cref="QuickUiWaitMs"/>.
        /// True once it ran; false when it failed, never runs (withdrawn) or had not ended in time (unknown: the page then asks the
        /// player to copy by hand).
        /// </summary>
        public static bool CopyToClipboard(string label, string text, Action<string, Exception> warn)
        {
            int result = AndroidJni.RunOnUiThread(() =>
            {
                IntPtr service = IntPtr.Zero, name = AndroidJni.Str("clipboard"), l = AndroidJni.Str(label), t = AndroidJni.Str(text), clip = IntPtr.Zero;
                try
                {
                    service = AndroidJni.CallObject(AndroidJni.AppContext(), AndroidJni.GetSystemService, new JValue(name));
                    if (service == IntPtr.Zero) throw new InvalidOperationException("no clipboard service");
                    clip = AndroidJni.CallStaticObject(AndroidJni.ClipNewPlainText, new JValue(l), new JValue(t));
                    AndroidJni.CallVoid(service, AndroidJni.ClipboardSetPrimary, new JValue(clip));
                }
                finally
                {
                    AndroidJni.Delete(clip);
                    AndroidJni.Delete(t);
                    AndroidJni.Delete(l);
                    AndroidJni.Delete(name);
                    AndroidJni.Delete(service);
                }
            }, QuickUiWaitMs, ex => warn("admin: copying to the clipboard failed", ex));
            return ClipboardResult(result);
        }

        /// <summary>Toast.makeText(context, text, LENGTH_LONG).show() on the UI thread; does not wait.</summary>
        public static void ShowToast(string text, Action<string, Exception> warn)
        {
            AndroidJni.RunOnUiThread(() =>
            {
                IntPtr t = AndroidJni.Str(text), toast = IntPtr.Zero;
                try
                {
                    toast = AndroidJni.CallStaticObject(AndroidJni.ToastMakeText, new JValue(AndroidJni.AppContext()), new JValue(t), new JValue(1));
                    if (toast != IntPtr.Zero) AndroidJni.CallVoid(toast, AndroidJni.ToastShow);
                }
                finally
                {
                    AndroidJni.Delete(toast);
                    AndroidJni.Delete(t);
                }
            }, 0, ex => warn("admin: the toast failed", ex));
        }

        private static class Libc
        {
            [DllImport("libc", SetLastError = true)]
            public static extern IntPtr write(int fd, byte[] buffer, IntPtr count);
        }
#endif
    }
}
