{
  description = "Jellystics Jellyfin plugin";
  inputs = { nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.05"; flake-utils.url = "github:numtide/flake-utils"; };
  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachSystem [ "x86_64-linux" "aarch64-linux" "aarch64-darwin" ] (system:
      let pkgs = import nixpkgs { inherit system; config.allowUnfree = true; };
      in { devShells.default = pkgs.mkShell { buildInputs = with pkgs; [ dotnetCorePackages.sdk_9_0-bin nodejs go-task docker sqlite ]; }; });
}
