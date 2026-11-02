using SweetHide.Core.Crypto;
using SweetHide.Core.Payloads;

namespace SweetHide.Core;

/// <summary>One of the two payloads of a deniable image, with its own password (and optional key file).</summary>
/// <param name="Payload">What this password reveals.</param>
/// <param name="Password">Must differ from the other part's password, unless the key files differ.</param>
/// <param name="KeyFile">Optional second factor for this part only.</param>
public sealed record DeniablePart(Payload Payload, string Password, KeyFile? KeyFile = null);
