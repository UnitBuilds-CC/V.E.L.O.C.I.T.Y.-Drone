using global::System.Security.Cryptography;

namespace Drone.Core.Custody;

/// <summary>
/// SHA-256 Merkle tree for batch verification of custody records.
/// Enables O(log N) verification of individual records instead of O(N) chain walk.
/// Compatible with the NMCP binary protocol's Merkle-signed frame format.
/// </summary>
public static class MerkleTree
{
    /// <summary>Size of a SHA-256 hash in bytes.</summary>
    public const int HashSize = 32;

    /// <summary>
    /// Compute the Merkle root from a list of leaf hashes.
    /// Each leaf must be exactly 32 bytes (SHA-256).
    /// Returns a 32-byte Merkle root.
    /// </summary>
    /// <param name="leaves">Array of 32-byte leaf hashes.</param>
    /// <returns>32-byte Merkle root hash.</returns>
    public static byte[] ComputeRoot(byte[][] leaves)
    {
        if (leaves == null || leaves.Length == 0)
            return SHA256.HashData(Array.Empty<byte>());

        if (leaves.Length == 1)
            return leaves[0];

        // Build tree bottom-up
        var currentLevel = leaves;

        while (currentLevel.Length > 1)
        {
            var nextLevel = new List<byte[]>();

            for (int i = 0; i < currentLevel.Length; i += 2)
            {
                if (i + 1 < currentLevel.Length)
                {
                    // Hash pair: left + right
                    var combined = new byte[HashSize * 2];
                    Buffer.BlockCopy(currentLevel[i], 0, combined, 0, HashSize);
                    Buffer.BlockCopy(currentLevel[i + 1], 0, combined, HashSize, HashSize);
                    nextLevel.Add(SHA256.HashData(combined));
                }
                else
                {
                    // Odd node: promote to next level (hash with itself for consistency)
                    var combined = new byte[HashSize * 2];
                    Buffer.BlockCopy(currentLevel[i], 0, combined, 0, HashSize);
                    Buffer.BlockCopy(currentLevel[i], 0, combined, HashSize, HashSize);
                    nextLevel.Add(SHA256.HashData(combined));
                }
            }

            currentLevel = nextLevel.ToArray();
        }

        return currentLevel[0];
    }

    /// <summary>
    /// Compute the Merkle root from hex-encoded leaf hashes.
    /// Convenience method for working with CustodyRecord.Hash values.
    /// </summary>
    /// <param name="hexLeaves">Array of hex-encoded SHA-256 hashes.</param>
    /// <returns>Hex-encoded Merkle root.</returns>
    public static string ComputeRootHex(string[] hexLeaves)
    {
        var leaves = hexLeaves.Select(h => Convert.FromHexString(h)).ToArray();
        var root = ComputeRoot(leaves);
        return Convert.ToHexString(root);
    }

    /// <summary>
    /// Build a Merkle proof for a specific leaf index.
    /// The proof allows verifying that a leaf is included in the tree without
    /// recomputing the entire tree.
    /// </summary>
    /// <param name="leaves">Array of 32-byte leaf hashes.</param>
    /// <param name="index">Index of the leaf to prove (0-based).</param>
    /// <returns>Array of 32-byte sibling hashes forming the proof path (bottom to top).</returns>
    public static byte[][] BuildProof(byte[][] leaves, int index)
    {
        if (leaves == null || leaves.Length == 0)
            return Array.Empty<byte[]>();

        if (leaves.Length == 1)
            return Array.Empty<byte[]>();

        if (index < 0 || index >= leaves.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        var proof = new List<byte[]>();
        var currentLevel = leaves;
        var currentIndex = index;

        while (currentLevel.Length > 1)
        {
            var nextLevel = new List<byte[]>();

            // Determine sibling index
            int siblingIndex;
            if (currentIndex % 2 == 0)
            {
                // Current is left child, sibling is right
                siblingIndex = currentIndex + 1;
                if (siblingIndex >= currentLevel.Length)
                    siblingIndex = currentIndex; // Odd node: sibling is self
            }
            else
            {
                // Current is right child, sibling is left
                siblingIndex = currentIndex - 1;
            }

            proof.Add(currentLevel[siblingIndex]);

            // Build next level
            for (int i = 0; i < currentLevel.Length; i += 2)
            {
                if (i + 1 < currentLevel.Length)
                {
                    var combined = new byte[HashSize * 2];
                    Buffer.BlockCopy(currentLevel[i], 0, combined, 0, HashSize);
                    Buffer.BlockCopy(currentLevel[i + 1], 0, combined, HashSize, HashSize);
                    nextLevel.Add(SHA256.HashData(combined));
                }
                else
                {
                    var combined = new byte[HashSize * 2];
                    Buffer.BlockCopy(currentLevel[i], 0, combined, 0, HashSize);
                    Buffer.BlockCopy(currentLevel[i], 0, combined, HashSize, HashSize);
                    nextLevel.Add(SHA256.HashData(combined));
                }
            }

            currentLevel = nextLevel.ToArray();
            currentIndex /= 2;
        }

        return proof.ToArray();
    }

    /// <summary>
    /// Verify a Merkle proof for a specific leaf.
    /// </summary>
    /// <param name="root">Expected 32-byte Merkle root.</param>
    /// <param name="leaf">32-byte leaf hash to verify.</param>
    /// <param name="proof">Array of 32-byte sibling hashes from BuildProof.</param>
    /// <param name="index">Index of the leaf in the original tree (0-based).</param>
    /// <returns>True if the proof is valid and the leaf is included in the tree.</returns>
    public static bool VerifyProof(byte[] root, byte[] leaf, byte[][] proof, int index)
    {
        if (root == null || leaf == null || proof == null)
            return false;

        if (root.Length != HashSize || leaf.Length != HashSize)
            return false;

        if (index < 0)
            return false;

        var current = leaf;

        for (int i = 0; i < proof.Length; i++)
        {
            if (proof[i].Length != HashSize)
                return false;

            var combined = new byte[HashSize * 2];

            if (index % 2 == 0)
            {
                // Current is left child
                Buffer.BlockCopy(current, 0, combined, 0, HashSize);
                Buffer.BlockCopy(proof[i], 0, combined, HashSize, HashSize);
            }
            else
            {
                // Current is right child
                Buffer.BlockCopy(proof[i], 0, combined, 0, HashSize);
                Buffer.BlockCopy(current, 0, combined, HashSize, HashSize);
            }

            current = SHA256.HashData(combined);
            index /= 2;
        }

        // Compare computed root with expected root
        if (current.Length != root.Length)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != root[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Verify a Merkle proof using hex-encoded values.
    /// </summary>
    public static bool VerifyProofHex(string rootHex, string leafHex, string[] proofHex, int index)
    {
        var root = Convert.FromHexString(rootHex);
        var leaf = Convert.FromHexString(leafHex);
        var proof = proofHex.Select(h => Convert.FromHexString(h)).ToArray();
        return VerifyProof(root, leaf, proof, index);
    }
}
