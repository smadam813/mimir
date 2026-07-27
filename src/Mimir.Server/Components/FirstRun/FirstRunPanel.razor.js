// The port's only JavaScript (#90). The Clipboard API is unavailable outside a secure context —
// http://localhost is one, a http:// LAN address is not — so this answers whether the write
// actually happened and the panel says so rather than silently claiming success.
export async function copyText(text) {
    if (!navigator.clipboard) {
        return false;
    }

    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
