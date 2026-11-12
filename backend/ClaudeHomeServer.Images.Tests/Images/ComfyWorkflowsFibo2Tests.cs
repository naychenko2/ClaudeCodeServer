using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Графы Fibo 2.0 против шаблонов ComfyUI 0.45.2 image_fibo2_edit_{dev,turbo}_pe.json и карточки
// briaai/Fibo-2.0-dev: файлы моделей, шаги и CFG, выходы узла условий, проводка картинок; генерация Dev —
// турбо-LoRA и расширитель VGL, рендер и доводка JSON-описания (Ш8)
public class ComfyWorkflowsFibo2Tests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    private static JsonArray Link(string node, int slot = 0) => new(node, slot);

    [Fact]
    public void ГенерацияDev_ФайлыКакВКарточке_ТурбоLoRaВосемьШаговБезCfg()
    {
        var wf = ComfyWorkflows.Fibo2Generate("ёжик в тумане", 1424, 800, 77, 1, "ccs/p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("fibo2_dev_int8_convrot.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_4b_int8_convrot.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("fibo2");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("flux2-vae.safetensors");

        // Турбо-LoRA между unet и сэмплером (замер Ш5: качество Dev за 5 с)
        Class(wf, "lora").Should().Be("LoraLoaderModelOnly");
        Inputs(wf, "lora")["model"]!.ToJsonString().Should().Be(Link("unet").ToJsonString());
        Inputs(wf, "lora")["lora_name"]!.GetValue<string>().Should().Be("fibo_2.0_turbo_lora_avg_rank_270_bf16.safetensors");
        Inputs(wf, "lora")["strength_model"]!.GetValue<double>().Should().Be(1.0);

        Class(wf, "cond").Should().Be("TextEncodeFibo2");
        var cond = Inputs(wf, "cond");
        cond["negative_prompt"]!.GetValue<string>().Should().BeEmpty("с LoRA CFG 1 — негатив не работает");
        cond["width"]!.GetValue<int>().Should().Be(1424);
        cond["height"]!.GetValue<int>().Should().Be(800);
        cond["batch_size"]!.GetValue<int>().Should().Be(1);
        cond.Should().NotContainKey("images.image_1");

        // Негатив и латент — второй и третий выходы узла условий
        var ks = Inputs(wf, "ks");
        ks["model"]!.ToJsonString().Should().Be(Link("lora").ToJsonString());
        ks["positive"]!.ToJsonString().Should().Be(Link("cond").ToJsonString());
        ks["negative"]!.ToJsonString().Should().Be(Link("cond", 1).ToJsonString());
        ks["latent_image"]!.ToJsonString().Should().Be(Link("cond", 2).ToJsonString());
        ks["steps"]!.GetValue<int>().Should().Be(8);
        ks["cfg"]!.GetValue<double>().Should().Be(1.0);
        ks["sampler_name"]!.GetValue<string>().Should().Be("euler");
        ks["scheduler"]!.GetValue<string>().Should().Be("simple");
        ks["seed"]!.GetValue<long>().Should().Be(77);
        Inputs(wf, "save")["images"]!.ToJsonString().Should().Be(Link("dec").ToJsonString());
    }

    [Fact]
    public void ГенерацияDev_РасширительVgl_ОписаниеВУзелУсловийИВИсторию()
    {
        var wf = ComfyWorkflows.Fibo2Generate("ёжик в тумане", 1424, 800, 77, 1, "ccs/p");

        Inputs(wf, "vgl_clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_8b_int8_convrot.safetensors");
        Class(wf, "vgl").Should().Be("TextGenerateV2");
        var vgl = Inputs(wf, "vgl");
        vgl["clip"]!.ToJsonString().Should().Be(Link("vgl_clip").ToJsonString());
        vgl["system_prompt"]!.GetValue<string>().Should().Be(ComfyWorkflows.Fibo2VglGenerateSystem)
            .And.NotContain("{style_preset_block}");
        vgl["prompt"]!.GetValue<string>().Should().Be(
            "Generate a detailed JSON object, adhering to the expected schema, for an imagined scene based on the following request: ёжик в тумане.");
        vgl["sampling_mode"]!.GetValue<string>().Should().Be("on");
        vgl["sampling_mode.temperature"]!.GetValue<double>().Should().Be(0.3);
        vgl["sampling_mode.seed"]!.GetValue<long>().Should().Be(77);
        vgl["max_length"]!.GetValue<int>().Should().Be(3000);

        // JSON уходит и в узел условий, и в историю ComfyUI — оттуда его берёт адаптер
        Inputs(wf, "cond")["prompt"]!.ToJsonString().Should().Be(Link("vgl").ToJsonString());
        Class(wf, "vgl_view").Should().Be("PreviewAny");
        Inputs(wf, "vgl_view")["source"]!.ToJsonString().Should().Be(Link("vgl").ToJsonString());
    }

    [Fact]
    public void Рендер_ГотовыйJsonБезРасширителя_ТемЖеСидом()
    {
        const string json = "{\"short_description\":\"ёжик\"}";
        var wf = ComfyWorkflows.Fibo2Render(json, 1248, 832, 101, "p");

        wf.Should().NotContainKey("vgl").And.NotContainKey("vgl_view");
        Inputs(wf, "cond")["prompt"]!.GetValue<string>().Should().Be(json, "текст описания — байт в байт: тот же текст и сид дают ту же картинку");
        Inputs(wf, "cond")["width"]!.GetValue<int>().Should().Be(1248);
        Inputs(wf, "ks")["model"]!.ToJsonString().Should().Be(Link("lora").ToJsonString());
        Inputs(wf, "ks")["seed"]!.GetValue<long>().Should().Be(101);
        Inputs(wf, "ks")["steps"]!.GetValue<int>().Should().Be(8);
    }

    [Fact]
    public void Доводка_РасширительПереписываетJsonПодИзменение_ЖадноТемЖеСидом()
    {
        const string json = "{\"background_setting\":\"street\"}";
        var wf = ComfyWorkflows.Fibo2Refine(json, "background and setting", "зимний лес", 1248, 832, 101, "p");

        var vgl = Inputs(wf, "vgl");
        vgl["system_prompt"]!.GetValue<string>().Should().Be(ComfyWorkflows.Fibo2VglRefineSystem);
        vgl["prompt"]!.GetValue<string>().Should().Be($"Current JSON:\n{json}\n\nChange: background and setting -> зимний лес");
        vgl["sampling_mode"]!.GetValue<string>().Should().Be("off", "доводка не должна менять сцену случайно");
        vgl.Should().NotContainKey("sampling_mode.seed");
        Inputs(wf, "cond")["prompt"]!.ToJsonString().Should().Be(Link("vgl").ToJsonString());
        Inputs(wf, "vgl_view")["source"]!.ToJsonString().Should().Be(Link("vgl").ToJsonString());
        Inputs(wf, "ks")["seed"]!.GetValue<long>().Should().Be(101);
        Inputs(wf, "ks")["model"]!.ToJsonString().Should().Be(Link("lora").ToJsonString());
    }

    [Fact]
    public void ГенерацияTurbo_4ШагаБезCfg_ПустойНегатив_Пачка()
    {
        var wf = ComfyWorkflows.Fibo2Generate("ёжик", 1024, 1024, 1, 3, "p", turbo: true);

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("fibo2_edit_turbo_int8_convrot.safetensors");
        Inputs(wf, "cond")["negative_prompt"]!.GetValue<string>().Should().BeEmpty();
        Inputs(wf, "cond")["batch_size"]!.GetValue<int>().Should().Be(3);
        Inputs(wf, "ks")["steps"]!.GetValue<int>().Should().Be(4);
        Inputs(wf, "ks")["cfg"]!.GetValue<double>().Should().Be(1.0);
        wf.Should().NotContainKey("lora", "Turbo — свой дистиллят, LoRA Dev к нему не идёт");
    }

    [Fact]
    public void Правка_КартинкиПоПорядку_ПустойНегатив_РазмерВыбираетУзел()
    {
        var wf = ComfyWorkflows.Fibo2Edit("сложи", ["a.png", "b.png", "c.png"], null, 5, "p", turbo: true);

        var cond = Inputs(wf, "cond");
        for (var k = 1; k <= 3; k++)
        {
            Class(wf, $"img{k}").Should().Be("LoadImage");
            cond[$"images.image_{k}"]!.ToJsonString().Should().Be(Link($"img{k}").ToJsonString());
        }
        Inputs(wf, "img1")["image"]!.GetValue<string>().Should().Be("a.png");
        cond["negative_prompt"]!.GetValue<string>().Should().BeEmpty("у правки негатив — null-подпись обучения");
        cond["width"]!.GetValue<int>().Should().Be(0);
        cond["height"]!.GetValue<int>().Should().Be(0);
        cond["vae"]!.ToJsonString().Should().Be(Link("vae").ToJsonString());
        cond["prompt"]!.GetValue<string>().Should().Contain("<image_3>").And.EndWith("сложи");
        wf.Should().NotContainKey("sim");
    }

    [Fact]
    public void Правка_ОднаКартинка_ПромптБезМеток_ХолстИзАргумента()
    {
        var wf = ComfyWorkflows.Fibo2Edit("добавь лодку", ["x.png"], (1152, 864), 1, "p", turbo: false);

        Inputs(wf, "cond")["prompt"]!.GetValue<string>().Should().Be("добавь лодку");
        Inputs(wf, "cond")["width"]!.GetValue<int>().Should().Be(1152);
        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("fibo2_dev_int8_convrot.safetensors");
        // Правка Dev — тоже через турбо-LoRA (проверка Ш8 на трёх правках: 10–15 с против ~120 с, без потерь)
        Inputs(wf, "lora")["lora_name"]!.GetValue<string>().Should().Be("fibo_2.0_turbo_lora_avg_rank_270_bf16.safetensors");
        Inputs(wf, "ks")["model"]!.ToJsonString().Should().Be(Link("lora").ToJsonString());
        Inputs(wf, "ks")["steps"]!.GetValue<int>().Should().Be(8);
        Inputs(wf, "ks")["cfg"]!.GetValue<double>().Should().Be(1.0);
        wf.Should().NotContainKey("vgl", "у правки расширителя нет");
    }

    [Fact]
    public void Правка_СЭталономПерсонажа_ДобавляетУзелСходства()
    {
        var wf = ComfyWorkflows.Fibo2Edit("он в кафе", ["x.png"], null, 1, "p", turbo: true, faceReferences: ["f1.png", "f2.png"]);

        Class(wf, "sim").Should().Be("CcsFaceSimilarity");
        Inputs(wf, "sim")["references"]!.GetValue<string>().Should().Be("f1.png\nf2.png");
    }

    [Fact]
    public void Правка_БольшеЧетырёхКартинок_Отказ()
    {
        var act = () => ComfyWorkflows.Fibo2Edit("сложи", Enumerable.Range(1, 5).Select(i => $"r{i}.png").ToList(), null, 1, "p", true);

        act.Should().Throw<ArgumentOutOfRangeException>();
        LocalImageModels.MaxImagesOf(LocalImageModels.Fibo2Turbo).Should().Be(4);
    }

    [Fact]
    public void Размеры_ОколоМегапикселя_КратныШестнадцати()
    {
        foreach (var (_, (w, h)) in ComfyWorkflows.OneMegapixelSizes)
        {
            (w % 16).Should().Be(0);
            (h % 16).Should().Be(0);
            ((double)w * h).Should().BeInRange(0.9 * 1024 * 1024, 1.1 * 1024 * 1024);
        }
    }
}
