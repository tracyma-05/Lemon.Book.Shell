# Lemon.Hub.Client

Signing in to the Lemon hub from any .NET 11 app, as a public OpenID Connect client: the authorization code flow
with PKCE, no client secret, refresh tokens, and session storage the app chooses. No UI and no Avalonia; AOT- and
trim-safe. The Lemon books get it through Lemon.Book.Shell; other apps take it on its own.

| Package | What is in it |
|---|---|
| `Lemon.Hub.Client` | `HubAccount` (sign in, keep the session, a current access token), `OidcClient`, `ISignInFlow`, `ISessionStore` with `KeyValueSessionStore` and `MemorySessionStore`, `AccountSession`, `PendingSignIn`, `SyncClientInfo`, `SignInException`, `SessionJson` |
| `Lemon.Hub.Client.Desktop` | `LoopbackSignInFlow`: the system browser, and the answer on `http://127.0.0.1:<port>/signin` (RFC 8252) |
| `Lemon.Hub.Client.Browser` | `RedirectSignInFlow`: the page goes to the sign-in service and comes back to its callback page |

## Using it

```csharp
var http = new HttpClient();
var account = new HubAccount(
    new KeyValueSessionStore(settings.Get, settings.Set),   // or a store of the app's own (below)
    new LoopbackSignInFlow(port: 47000),                     // the port the client is registered with
    http);

// How to sign in: from the hub (anonymous), or the app's own SyncClientInfo(authority, clientId, scope).
var client = await account.Oidc.GetClientInfoAsync(OidcClient.ClientInfoAddress(hub, "my-app"), cancellationToken);
await account.SignInAsync(client, cancellationToken);

// Each call to the hub: a current token, refreshed when due (overlapping refreshes share one request).
request.Headers.Authorization = new("Bearer", await account.GetAccessTokenAsync(cancellationToken));
```

- `SignInException.NeedsSignIn`: not signed in, or the refresh was refused; ask the user to sign in again.
  `HttpRequestException`: offline, try later.
- A 401 from the hub before the token's time: `account.Expire()`, and the next call refreshes.
- `account.SignOut()` forgets the session on this device.
- On the web, `SignInAsync` returns null: the answer comes to the app's next start. Its callback page leaves the
  answer's query for the app, which passes it to `account.CompleteAsync(answer, …)`. The pending sign-in is in the
  session store, so that store must outlive the page (`localStorage`).

## Keeping the session

`ISessionStore` is the app's: `KeyValueSessionStore` keeps the session and the pending sign-in as JSON text under two
keys of any string store, and `MemorySessionStore` keeps nothing past the process. To protect the refresh token, wrap
the store's get and set (decrypt and encrypt the text, e.g. with the operating system's per-user data protection), or
implement `ISessionStore` and use `SessionJson` for the format.

## Licence

MIT.
