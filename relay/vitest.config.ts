import { defineConfig } from "vitest/config";
import { cloudflareTest } from "@cloudflare/vitest-pool-workers";

export default defineConfig({
  plugins: [cloudflareTest({
    wrangler: { configPath: "./wrangler.jsonc" },
    miniflare: { bindings: { CONTROLLER_PUBLIC_KEY: "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE19IJpleMRXsNXWgvgqOigzVOON1teOo3sLF+M7fPnL2kbAWXbnLTaYlL4vipc57+ElM30kB2bg84XoYDWWc2Zw==", ACCESS_KEY: "a".repeat(64), PROTECTED_ACCESS_KEY: "d".repeat(64) } }
  })],
  test: { testTimeout: 15000 }
});
