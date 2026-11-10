# Google Drive backup — one-time setup

Chartwell backs up to **each user's own Google Drive**, into a hidden app-only folder (`appDataFolder`).
No Chartwell server is involved; the app talks to Google directly after the user signs in on the phone.

Because the app signs in as *itself*, Google needs to know about it. This is a **one-time developer
setup per app build**, not something every user does: once a build carries a client id, anyone who
installs it just taps *Sign in with Google*. It takes about 15 minutes and is free (no billing account
needed; the backup counts against each user's own Drive storage).

## 1. Create the Google Cloud project

1. Open <https://console.cloud.google.com> and sign in.
2. Top bar → project picker → **New project** → name it `Chartwell` → **Create**.
3. **APIs & Services → Library** → search *Google Drive API* → **Enable**.

## 2. OAuth consent screen

1. **APIs & Services → OAuth consent screen** → User type **External** → **Create**.
2. App name `Chartwell`, your email for both support and developer contact → **Save and continue**.
3. **Scopes → Add or remove scopes** → tick
   - `.../auth/drive.appdata`
   - `openid`
   - `.../auth/userinfo.email`
   → **Update** → **Save and continue**.
4. **Test users → Add users** → add your Google account → **Save and continue**.

### Testing vs. In production

While the publishing status is **Testing**:

- only accounts listed as test users (max 100) can sign in, and
- Google expires refresh tokens after **7 days** (because `drive.appdata` goes beyond basic profile
  scopes), so automatic backup silently stops about a week after sign-in until the user signs in again.

So, even for personal use, click **Publish app** to move it to **In production**:

- `drive.appdata` is a non-sensitive scope, so no Google security review is required.
- Until the brand is verified, users see an "unverified app" screen at sign-in (Advanced → continue).
  To remove it and show your name/logo, submit brand verification: it needs a home page, a privacy
  policy URL and an authorized domain you own (a GitHub Pages site is enough).

## 3. Create the Android OAuth clients

An Android OAuth client is tied to one package name and one signing-certificate SHA-1, so you need
**one client for debug builds and one for release builds**.

For each:

1. **APIs & Services → Credentials → Create credentials → OAuth client ID**.
2. Application type **Android**.
3. Package name: `com.healthapp.healthtracker`
4. SHA-1 certificate fingerprint (see below).
5. **Advanced settings → tick "Enable custom URI scheme".** It is off by default for new Android
   clients, and the app returns from sign-in through a custom scheme
   (`com.googleusercontent.apps.<id>:/oauth2redirect`). Without it Google rejects the redirect.
6. **Create** and copy the **Client ID** (it looks like `123456789-abc123.apps.googleusercontent.com`).

### Which SHA-1

`keytool` ships with any JDK (e.g. `C:\Program Files\Java\jdk-*\bin` or `C:\Program Files\Microsoft\jdk-*\bin`).

**Debug** — Visual Studio signs .NET MAUI debug builds with the Xamarin debug keystore, *not*
`%USERPROFILE%\.android\debug.keystore`:

```powershell
keytool -list -v -keystore "$env:LOCALAPPDATA\Xamarin\Mono for Android\debug.keystore" -alias androiddebugkey -storepass android
```

**Release**

- Sideloaded APK: the SHA-1 of the keystore you sign the release with
  (`keytool -list -v -keystore <your.keystore> -alias <alias>`).
- Google Play: Play re-signs the app, so use the **App signing key certificate** SHA-1 from
  Play Console → *Test and release → App integrity*, not your upload key.

## 4. Put the client ids in the app

Open `HealthTracker/Constants/CloudConstants.cs` and replace the placeholders with the part of each
client id before `.apps.googleusercontent.com`:

```csharp
private const string DebugClientIdPrefix = "123456789-abc123";
private const string ReleaseClientIdPrefix = "123456789-def456";
```

`#if DEBUG` picks the right one; `GoogleClientId` and `RedirectScheme` derive from it, so nothing
else changes. A configuration whose prefix is still the placeholder shows cloud backup as
"not configured". Rebuild and deploy.

## 5. Use it

Settings → **Google Drive backup** → **Sign in with Google**. Then:

- **Back up now** uploads a snapshot (database + all report/prescription files) and replaces the previous one.
  Do this once on each device: automatic backup only starts after a manual backup or a restore on
  that device, so a fresh install can never overwrite the cloud copy with an empty database.
- **Automatic daily backup** uploads at most once every 24 hours, when the app is opened or left and
  the phone is online. It does not run while the app is never opened.
- **Restore from Drive** replaces everything on the phone with the latest snapshot (there is a
  confirmation; the app restarts its UI afterwards).

To move to a new phone: install the same build, sign in with the same Google account, **Restore from Drive**.

## Privacy notes

- The only Drive permission requested is `drive.appdata`: the app cannot see or touch any other file in your Drive, and you cannot see the backup in the Drive UI either (it is in Google's hidden per-app space). Deleting the app's data from Drive: Google Drive → Settings → Manage apps → Chartwell → Delete hidden app data.
- Tokens are kept in Android's encrypted `SecureStorage`; the backup ZIP itself is not additionally encrypted beyond Google's at-rest encryption.
