using Dam.Domain.Assets;
using Dam.Infrastructure.Delivery;
using Dam.UnitTests.Support;
using NetVips;

namespace Dam.UnitTests;

public sealed class DeliveryTokenTests
{
    private const string Key = "unit-test-signing-key-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid(), Asset = Guid.NewGuid();
    private static DeliveryClaims Claims(string scope = "r:thumb,web", DateTimeOffset? expires = null, string? ip = null, bool wm = false) =>
        new(Tenant, Asset, scope, expires ?? Now.AddMinutes(30), ip, wm);

    [Fact]
    public void A_signed_token_round_trips_with_everything_it_granted()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var (c, failure) = signer.Verify(signer.Sign(Claims(ip: "203.0.113.9", wm: true)), Now);
        Assert.Equal(TokenFailure.None, failure);
        Assert.Equal((Tenant, Asset, "r:thumb,web", "203.0.113.9", true), (c!.TenantId, c.AssetId, c.Scope, c.Ip, c.Watermark));
        Assert.True(c.AllowsRendition("thumb"));
        Assert.False(c.AllowsRendition("original"));
        Assert.Null(c.TransformScope);
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var token = signer.Sign(Claims(expires: Now.AddSeconds(5)));
        Assert.Equal(TokenFailure.None, signer.Verify(token, Now).Failure);
        Assert.Equal(TokenFailure.Expired, signer.Verify(token, Now.AddSeconds(5)).Failure);
    }

    [Fact]
    public void Changing_any_part_of_the_token_breaks_the_signature()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var token = signer.Sign(Claims());
        var (body, sig) = (token.Split('.')[0], token.Split('.')[1]);

        var widened = new DeliveryTokenSigner([Key]).Sign(Claims("r:original"));
        Assert.Equal(TokenFailure.BadSignature, signer.Verify(widened.Split('.')[0] + "." + sig, Now).Failure);   // new scope, old signature
        var flipped = body[..^1] + (body[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(TokenFailure.BadSignature, signer.Verify(flipped + "." + sig, Now).Failure);
        Assert.Equal(TokenFailure.BadSignature, signer.Verify(body + "." + sig[..^2] + "AA", Now).Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData(".")]
    [InlineData("a.")]
    [InlineData(".b")]
    [InlineData("!!!.???")]
    public void Garbage_is_malformed_never_an_exception(string token) =>
        Assert.NotEqual(TokenFailure.None, new DeliveryTokenSigner([Key]).Verify(token, Now).Failure);

    [Fact]
    public void A_different_key_cannot_forge_or_verify_but_a_rotated_in_key_list_still_verifies_old_links()
    {
        var old = new DeliveryTokenSigner([Key]);
        var token = old.Sign(Claims());
        var stranger = new DeliveryTokenSigner(["a-completely-different-key-0123456789abcdef"]);
        Assert.Equal(TokenFailure.BadSignature, stranger.Verify(token, Now).Failure);

        var rotated = new DeliveryTokenSigner(["new-key-for-signing-0123456789abcdefgh", Key]);   // new key signs, old key still verifies
        Assert.Equal(TokenFailure.None, rotated.Verify(token, Now).Failure);
        Assert.Equal(TokenFailure.BadSignature, old.Verify(rotated.Sign(Claims()), Now).Failure);
    }

    [Fact]
    public void Weak_or_missing_keys_are_rejected_up_front()
    {
        Assert.Throws<ArgumentException>(() => new DeliveryTokenSigner([]));
        Assert.Throws<ArgumentException>(() => new DeliveryTokenSigner(["short"]));
    }

    [Fact]
    public void A_transform_scope_is_reported_exactly_as_signed()
    {
        var signer = new DeliveryTokenSigner([Key]);
        var (c, _) = signer.Verify(signer.Sign(Claims("t:w=800&fit=cover&fmt=webp&q=80")), Now);
        Assert.Equal("w=800&fit=cover&fmt=webp&q=80", c!.TransformScope);
        Assert.False(c.AllowsRendition("thumb"));
    }
}

public sealed class TransformSpecTests
{
    [Fact]
    public void Defaults_are_filled_in_and_the_canonical_form_is_stable_whatever_order_or_case_the_client_used()
    {
        var (a, _) = TransformSpec.Parse("800", null, null, null, null, null);
        var (b, _) = TransformSpec.Parse("800", "", "COVER", "WEBP", "80", null);
        Assert.Equal("w=800&fit=cover&fmt=webp&q=80", a!.Canonical());
        Assert.Equal(a.Canonical(), b!.Canonical());
        Assert.Equal("w=400&h=300&fit=contain&fmt=jpg&q=70&focal=0.3,0.6", TransformSpec.Parse("400", "300", "contain", "jpeg", "70", "0.3,0.6").Spec!.Canonical());
    }

    [Theory]
    [InlineData(null, null, null, null, null, null)]     // no size at all
    [InlineData("0", null, null, null, null, null)]
    [InlineData("9000", null, null, null, null, null)]   // over the cap
    [InlineData("-5", null, null, null, null, null)]
    [InlineData("abc", null, null, null, null, null)]
    [InlineData("100", null, "stretch", null, null, null)]
    [InlineData("100", null, null, "gif", null, null)]
    [InlineData("100", null, null, null, "0", null)]
    [InlineData("100", null, null, null, "101", null)]
    [InlineData("100", null, null, null, null, "1.5,0.5")]
    [InlineData("100", null, null, null, null, "0.5")]
    public void Invalid_parameters_give_an_error_message_instead_of_a_transform(string? w, string? h, string? fit, string? fmt, string? q, string? focal)
    {
        var (spec, error) = TransformSpec.Parse(w, h, fit, fmt, q, focal);
        Assert.Null(spec);
        Assert.False(string.IsNullOrEmpty(error));
    }
}

public sealed class ImageRendererTests
{
    private static Image Load(byte[] b) => Image.NewFromBuffer(b);

    [Fact]
    public void A_profile_shrinks_to_its_longest_edge_keeping_the_shape_and_converts_to_its_format()
    {
        var wide = TestImages.Resize(TestImages.Disc(600), 600);
        using var src = Image.NewFromBuffer(wide);
        var wideSource = src.Embed(0, 100, 600, 400, extend: Enums.Extend.White).WriteToBuffer(".png");    // 600x400
        var r = ImageRenderer.Render(wideSource, RenderRequest.ForProfile(RenditionProfiles.Images.First(p => p.Name == "thumb")!));
        Assert.Equal("image/webp", r.Mime);
        Assert.Equal((240, 160), (r.Width, r.Height));
        using var back = Load(r.Bytes);
        Assert.Equal((240, 160), (back.Width, back.Height));
    }

    [Fact]
    public void A_profile_never_enlarges_a_small_image()
    {
        var r = ImageRenderer.Render(TestImages.Disc(100), RenderRequest.ForProfile(RenditionProfiles.Images.First(p => p.Name == "web")!));
        Assert.Equal((100, 100), (r.Width, r.Height));
    }

    [Fact]
    public void Cover_fills_the_exact_box_and_the_focal_point_decides_which_part_survives()
    {
        // left half black, right half white, 400x200
        var pixels = new byte[400 * 200];
        for (var y = 0; y < 200; y++) for (var x = 200; x < 400; x++) pixels[y * 400 + x] = 255;
        using var halves = Image.NewFromMemory(pixels, 400, 200, 1, Enums.BandFormat.Uchar);
        var source = halves.WriteToBuffer(".png");

        double Mean(byte[] b) { using var i = Load(b); return i.Avg(); }
        var left = ImageRenderer.Render(source, new RenderRequest(100, 100, "cover", "png", 80, 0.0, 0.5));
        var right = ImageRenderer.Render(source, new RenderRequest(100, 100, "cover", "png", 80, 1.0, 0.5));
        Assert.Equal((100, 100), (left.Width, left.Height));
        Assert.Equal((100, 100), (right.Width, right.Height));
        Assert.True(Mean(left.Bytes) < 30, "focal 0 should keep the black side");
        Assert.True(Mean(right.Bytes) > 225, "focal 1 should keep the white side");
    }

    [Fact]
    public void Contain_stays_inside_the_box_and_fill_stretches_to_it()
    {
        var src = TestImages.Disc(400);                                       // 400x400
        var contain = ImageRenderer.Render(src, new RenderRequest(200, 100, "contain", "jpg", 80));
        Assert.Equal((100, 100), (contain.Width, contain.Height));
        var fill = ImageRenderer.Render(src, new RenderRequest(200, 100, "fill", "jpg", 80, AllowUpscale: true));
        Assert.Equal((200, 100), (fill.Width, fill.Height));
    }

    [Fact]
    public void A_single_dimension_keeps_the_aspect_ratio()
    {
        using var s = Image.NewFromBuffer(TestImages.Disc(300));
        var tall = s.Embed(0, 0, 300, 600, extend: Enums.Extend.Black).WriteToBuffer(".png");
        var r = ImageRenderer.Render(tall, new RenderRequest(null, 300, "cover", "webp", 80));
        Assert.Equal((150, 300), (r.Width, r.Height));
    }

    [Fact]
    public void Transparent_images_flatten_onto_white_for_jpeg_and_keep_alpha_for_png()
    {
        using var clear = Image.Black(64, 64).Bandjoin(0);      // fully transparent black
        var source = clear.Copy(interpretation: Enums.Interpretation.Srgb).WriteToBuffer(".png");
        using var jpg = Load(ImageRenderer.Render(source, new RenderRequest(64, 64, "contain", "jpg", 90)).Bytes);
        Assert.True(jpg.Avg() > 240, "transparent pixels should become white in a JPEG");
        using var png = Load(ImageRenderer.Render(source, new RenderRequest(64, 64, "contain", "png", 90)).Bytes);
        Assert.True(png.HasAlpha());
    }

    [Fact]
    public void Output_carries_no_embedded_metadata_so_camera_and_location_details_do_not_leak()
    {
        var withExif = TestImages.WithExif(TestImages.Disc(200), "AcmeCam", "Model 7");
        using var before = Load(withExif);
        Assert.NotEqual(0, before.GetTypeOf("exif-ifd0-Make"));
        using var after = Load(ImageRenderer.Render(withExif, new RenderRequest(200, 200, "contain", "jpg", 80)).Bytes);
        Assert.Equal(0, after.GetTypeOf("exif-ifd0-Make"));
    }

    [Fact]
    public void A_watermark_visibly_changes_the_picture_without_changing_its_size()
    {
        var src = TestImages.Disc(400);
        var plain = ImageRenderer.Render(src, new RenderRequest(400, 400, "contain", "png", 90));
        var marked = ImageRenderer.Render(src, new RenderRequest(400, 400, "contain", "png", 90, Watermark: true));
        Assert.Equal((plain.Width, plain.Height), (marked.Width, marked.Height));
        using var a = Load(plain.Bytes); using var b = Load(marked.Bytes);
        using var diff = (a.Flatten().Colourspace(Enums.Interpretation.Bw) - b.Flatten().Colourspace(Enums.Interpretation.Bw)).Abs();
        Assert.True(diff.Avg() > 1.0, $"watermark should alter the pixels (mean difference {diff.Avg():0.00})");
    }

    [Fact]
    public void Bytes_that_are_not_an_image_raise_a_specific_error()
    {
        Assert.Throws<UnsupportedImageException>(() => ImageRenderer.Render("not an image"u8.ToArray(), new RenderRequest(100, 100, "contain", "webp", 80)));
    }
}
