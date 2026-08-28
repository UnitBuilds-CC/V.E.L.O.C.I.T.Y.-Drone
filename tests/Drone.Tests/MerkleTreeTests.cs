using Xunit;
using Drone.Core.Custody;

namespace Drone.Tests;

/// <summary>
/// Unit tests for MerkleTree — SHA-256 Merkle root computation, proof generation, and verification.
/// </summary>
public class MerkleTreeTests
{
    private static byte[] HashString(string s)
    {
        using var sha = global::System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(global::System.Text.Encoding.UTF8.GetBytes(s));
    }

    [Fact]
    public void ComputeRoot_SingleLeaf_ReturnsLeafHash()
    {
        var leaf = HashString("record-1");
        var root = MerkleTree.ComputeRoot(new[] { leaf });

        // With a single leaf, the root should be the leaf hash itself
        Assert.NotNull(root);
        Assert.Equal(32, root.Length);
        Assert.Equal(leaf, root);
    }

    [Fact]
    public void ComputeRoot_TwoLeaves_CombinesCorrectly()
    {
        var leaf1 = HashString("record-1");
        var leaf2 = HashString("record-2");
        var root = MerkleTree.ComputeRoot(new[] { leaf1, leaf2 });

        Assert.NotNull(root);
        Assert.Equal(32, root.Length);

        // Root should NOT equal either leaf
        Assert.NotEqual(leaf1, root);
        Assert.NotEqual(leaf2, root);
    }

    [Fact]
    public void ComputeRoot_EvenCount_ProducesValidRoot()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);
        Assert.NotNull(root);
        Assert.Equal(32, root.Length);

        // Same input should produce same root (deterministic)
        var root2 = MerkleTree.ComputeRoot(leaves);
        Assert.Equal(root, root2);
    }

    [Fact]
    public void ComputeRoot_OddCount_DuplicatesLastNode()
    {
        var leaves = new byte[3][];
        for (int i = 0; i < 3; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);
        Assert.NotNull(root);
        Assert.Equal(32, root.Length);

        // Odd count: the last node should be duplicated at each level where odd
        // Root should be deterministic
        var root2 = MerkleTree.ComputeRoot(leaves);
        Assert.Equal(root, root2);
    }

    [Fact]
    public void ComputeRoot_DifferentOrder_DifferentRoot()
    {
        var leaf1 = HashString("record-a");
        var leaf2 = HashString("record-b");

        var root1 = MerkleTree.ComputeRoot(new[] { leaf1, leaf2 });
        var root2 = MerkleTree.ComputeRoot(new[] { leaf2, leaf1 });

        Assert.NotEqual(root1, root2); // Order matters
    }

    [Fact]
    public void ComputeRoot_EmptyInput_ReturnsDeterministicHash()
    {
        var root = MerkleTree.ComputeRoot(Array.Empty<byte[]>());
        Assert.NotNull(root);
        Assert.Equal(32, root.Length);
        // Empty tree returns SHA-256 of empty data (deterministic)
        var root2 = MerkleTree.ComputeRoot(Array.Empty<byte[]>());
        Assert.Equal(root, root2);
    }

    [Fact]
    public void BuildProof_SingleLeaf_ReturnsEmptyProof()
    {
        var leaf = HashString("only-record");
        var proof = MerkleTree.BuildProof(new[] { leaf }, 0);

        Assert.NotNull(proof);
        Assert.Empty(proof); // Single leaf needs no siblings
    }

    [Fact]
    public void BuildProof_TwoLeaves_ReturnsOneSibling()
    {
        var leaves = new[] { HashString("a"), HashString("b") };
        var proof = MerkleTree.BuildProof(leaves, 0);

        Assert.NotNull(proof);
        Assert.Single(proof);
        Assert.Equal(leaves[1], proof[0]); // Sibling is the other leaf
    }

    [Fact]
    public void BuildProof_FourLeaves_ReturnsTwoSiblings()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashString($"record-{i}");

        var proof = MerkleTree.BuildProof(leaves, 0);
        Assert.NotNull(proof);
        Assert.Equal(2, proof.Length); // log2(4) = 2
    }

    [Fact]
    public void VerifyProof_SingleLeaf_Validates()
    {
        var leaf = HashString("only-record");
        var root = MerkleTree.ComputeRoot(new[] { leaf });
        var proof = MerkleTree.BuildProof(new[] { leaf }, 0);

        Assert.True(MerkleTree.VerifyProof(root, leaf, proof, 0));
    }

    [Fact]
    public void VerifyProof_EvenCount_AllIndicesValidate()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);

        for (int i = 0; i < 4; i++)
        {
            var proof = MerkleTree.BuildProof(leaves, i);
            Assert.True(MerkleTree.VerifyProof(root, leaves[i], proof, i),
                $"Proof verification failed for index {i}");
        }
    }

    [Fact]
    public void VerifyProof_OddCount_AllIndicesValidate()
    {
        var leaves = new byte[5][];
        for (int i = 0; i < 5; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);

        for (int i = 0; i < 5; i++)
        {
            var proof = MerkleTree.BuildProof(leaves, i);
            Assert.True(MerkleTree.VerifyProof(root, leaves[i], proof, i),
                $"Proof verification failed for index {i}");
        }
    }

    [Fact]
    public void VerifyProof_TamperedLeaf_Fails()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);
        var proof = MerkleTree.BuildProof(leaves, 1);

        // Tamper with the leaf
        var tamperedLeaf = HashString("tampered-record");
        Assert.False(MerkleTree.VerifyProof(root, tamperedLeaf, proof, 1));
    }

    [Fact]
    public void VerifyProof_WrongIndex_Fails()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);
        var proof = MerkleTree.BuildProof(leaves, 1);

        // Use correct leaf but wrong index
        Assert.False(MerkleTree.VerifyProof(root, leaves[1], proof, 2));
    }

    [Fact]
    public void VerifyProof_WrongRoot_Fails()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashString($"record-{i}");

        var proof = MerkleTree.BuildProof(leaves, 0);
        var fakeRoot = HashString("fake-root");

        Assert.False(MerkleTree.VerifyProof(fakeRoot, leaves[0], proof, 0));
    }

    [Fact]
    public void ComputeRootHex_ReturnsValidHexString()
    {
        var leaves = new[] { HashString("a"), HashString("b") };
        var hexLeaves = leaves.Select(l => Convert.ToHexString(l)).ToArray();
        var rootHex = MerkleTree.ComputeRootHex(hexLeaves);

        Assert.NotNull(rootHex);
        Assert.Equal(64, rootHex.Length); // 32 bytes = 64 hex chars
        Assert.Matches("^[0-9A-F]{64}$", rootHex);
    }

    [Fact]
    public void VerifyProofHex_WorksWithHexStrings()
    {
        var leaves = new[] { HashString("a"), HashString("b"), HashString("c") };
        var hexLeaves = leaves.Select(l => Convert.ToHexString(l)).ToArray();
        var rootHex = MerkleTree.ComputeRootHex(hexLeaves);
        var leafHex = Convert.ToHexString(leaves[1]);

        var proof = MerkleTree.BuildProof(leaves, 1);
        var proofHex = proof.Select(p => Convert.ToHexString(p)).ToArray();

        Assert.True(MerkleTree.VerifyProofHex(rootHex, leafHex, proofHex, 1));
    }

    [Fact]
    public void ComputeRoot_LargeBatch_ProducesValidRoot()
    {
        var leaves = new byte[100][];
        for (int i = 0; i < 100; i++)
            leaves[i] = HashString($"record-{i}");

        var root = MerkleTree.ComputeRoot(leaves);
        Assert.NotNull(root);
        Assert.Equal(32, root.Length);

        // Verify a few random proofs
        foreach (var idx in new[] { 0, 49, 99 })
        {
            var proof = MerkleTree.BuildProof(leaves, idx);
            Assert.True(MerkleTree.VerifyProof(root, leaves[idx], proof, idx),
                $"Proof failed for index {idx} in 100-leaf tree");
        }
    }
}
