namespace ABook.Agents;

/// <summary>
/// JSON schemas for structured output from planning agents. Used with CreateJsonSchemaFormat
/// to constrain the LLM response shape and prevent type mismatches (e.g., object vs array).
/// </summary>
internal static class JsonSchemas
{
    public const string StoryBible = """
        {
          "type": "object",
          "properties": {
            "settingDescription": { "type": "string" },
            "timePeriod": { "type": "string" },
            "themes": { "type": "string" },
            "toneAndStyle": { "type": "string" },
            "worldRules": { "type": "string" },
            "notes": { "type": "string" }
          },
          "required": ["settingDescription", "timePeriod", "themes", "toneAndStyle", "worldRules", "notes"],
          "additionalProperties": false
        }
        """;

    public const string Characters = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "role": { "type": "string" },
              "physicalDescription": { "type": "string" },
              "personality": { "type": "string" },
              "backstory": { "type": "string" },
              "goalMotivation": { "type": "string" },
              "arc": { "type": "string" },
              "firstAppearanceChapterNumber": { "type": ["integer", "null"] },
              "notes": { "type": "string" }
            },
            "required": ["name", "role"],
            "additionalProperties": false
          }
        }
        """;

    public const string PlotThreads = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "type": { "type": "string" },
              "status": { "type": "string" },
              "description": { "type": "string" },
              "introducedChapterNumber": { "type": ["integer", "null"] },
              "resolvedChapterNumber": { "type": ["integer", "null"] }
            },
            "required": ["name", "type"],
            "additionalProperties": false
          }
        }
        """;

    public const string ChapterOutlines = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "number": { "type": "integer" },
              "title": { "type": "string" },
              "outline": { "type": "string" },
              "povCharacter": { "type": ["string", "null"] },
              "charactersInvolved": { "type": "array", "items": { "type": "string" } },
              "plotThreads": { "type": "array", "items": { "type": "string" } },
              "foreshadowingNotes": { "type": ["string", "null"] },
              "payoffNotes": { "type": ["string", "null"] }
            },
            "required": ["number", "title", "outline"],
            "additionalProperties": false
          }
        }
        """;

    public const string CharacterVisuals = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "visualPrompt": { "type": "string" },
              "usualOutfit": { "type": "string" }
            },
            "required": ["name", "visualPrompt", "usualOutfit"],
            "additionalProperties": false
          }
        }
        """;

    public const string ChapterIllustrations = """
        {
          "type": "object",
          "properties": {
            "illustrations": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "paragraphNumber": { "type": "integer" },
                  "anchorText": { "type": "string" },
                  "caption": { "type": "string" },
                  "characters": { "type": "array", "items": { "type": "string" } },
                  "focusCharacter": { "type": "string" },
                  "assets": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "kind": { "type": "string", "enum": ["object", "outfit", "location", "creature", "vehicle", "other"] },
                        "description": { "type": "string" }
                      },
                      "required": ["name", "kind", "description"],
                      "additionalProperties": false
                    }
                  },
                  "prompt": { "type": "string" },
                  "negativePrompt": { "type": "string" },
                  "orientation": { "type": "string", "enum": ["portrait", "landscape", "square"] }
                },
                "required": ["paragraphNumber", "anchorText", "caption", "characters", "focusCharacter", "assets", "prompt", "negativePrompt", "orientation"],
                "additionalProperties": false
              }
            }
          },
          "required": ["illustrations"],
          "additionalProperties": false
        }
        """;

    public const string Cover = """
        {
          "type": "object",
          "properties": {
            "characters": { "type": "array", "items": { "type": "string" } },
            "focusCharacter": { "type": "string" },
            "assets": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "kind": { "type": "string", "enum": ["object", "outfit", "location", "creature", "vehicle", "other"] },
                  "description": { "type": "string" }
                },
                "required": ["name", "kind", "description"],
                "additionalProperties": false
              }
            },
            "prompt": { "type": "string" },
            "negativePrompt": { "type": "string" },
            "orientation": { "type": "string", "enum": ["portrait", "landscape", "square"] }
          },
          "required": ["characters", "focusCharacter", "assets", "prompt", "negativePrompt", "orientation"],
          "additionalProperties": false
        }
        """;

    public const string StyleSheet = """
        {
          "type": "object",
          "properties": {
            "style": { "type": "string" },
            "portraitFraming": { "type": "string" },
            "negativePrompt": { "type": "string" }
          },
          "required": ["style", "portraitFraming", "negativePrompt"],
          "additionalProperties": false
        }
        """;
}
